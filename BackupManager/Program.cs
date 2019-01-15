﻿using Google.Apis.Auth.OAuth2;
using Google.Apis.Drive.v3;
using Google.Apis.Services;
using Renci.SshNet;
using Renci.SshNet.Common;
using Renci.SshNet.Sftp;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Xml;
using System.Xml.Serialization;
using GFile = Google.Apis.Drive.v3.Data.File;

namespace BackupManager
{
    public static class Program
    {
        public readonly static Stopwatch sw = new Stopwatch();

        private const string FOLDER_MIME = @"application/vnd.google-apps.folder";

        private const string CONFIG_NAME = @"FlashiiBackupManager.v1.xml";

        public static bool IsWindows
            => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

        public readonly static DateTimeOffset Startup = DateTimeOffset.UtcNow;

        public static string Basename
            => $@"{Environment.MachineName} {Startup.Year:0000}-{Startup.Month:00}-{Startup.Day:00} {Startup.Hour:00}{Startup.Minute:00}{Startup.Second:00}";
        public static string DatabaseDumpName
            => $@"{Basename}.sql.gz";
        public static string UserDataName
            => $@"{Basename}.zip";

        private static Config Config;
        private readonly static string ConfigPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Personal),
            CONFIG_NAME
        );

        private static DriveService DriveService;
        private static object BackupStorage;

        private static SftpClient SFTP;

        public static bool Headless;

        public static string WindowsToUnixPath(this string path)
        {
            return IsWindows ? path.Replace('\\', '/') : path;
        }

        public static Stream ToXml(this object obj, bool pretty = false)
        {
            MemoryStream ms = new MemoryStream();
            XmlSerializer xs = new XmlSerializer(obj.GetType());

            using (XmlWriter xw = XmlWriter.Create(ms, new XmlWriterSettings { Indent = pretty }))
                xs.Serialize(xw, obj);

            ms.Seek(0, SeekOrigin.Begin);
            return ms;
        }

        public static T FromXml<T>(Stream xml)
        {
            if (xml.CanSeek)
                xml.Seek(0, SeekOrigin.Begin);

            XmlSerializer xs = new XmlSerializer(typeof(T));
            return (T)xs.Deserialize(xml);
        }

        public static void SaveConfig()
        {
            Log(@"Saving configuration...");
            using (FileStream fs = new FileStream(ConfigPath, FileMode.Create, FileAccess.Write))
            using (Stream cs = Config.ToXml(true))
                cs.CopyTo(fs);
        }

        public static void LoadConfig()
        {
            Log(@"Loading configuration...");
            using (FileStream fs = File.OpenRead(ConfigPath))
                Config = FromXml<Config>(fs);
        }

        public static void Main(string[] args)
        {
            Headless = args.Contains(@"-cron") || args.Contains(@"-headless");

            Log(@"Flashii Backup Manager");
            sw.Start();

            if (!File.Exists(ConfigPath))
            {
                Config = new Config();
                SaveConfig();
                Error(@"No configuration file exists, created a blank one. Be sure to fill it out properly.");
            }

            LoadConfig();

            switch (Config.StorageMethod)
            {
                case StorageMethod.GoogleDrive:
                    UserCredential uc = GoogleAuthenticate(
                        new ClientSecrets
                        {
                            ClientId = Config.GoogleClientId,
                            ClientSecret = Config.GoogleClientSecret,
                        },
                        new[] {
                            DriveService.Scope.Drive,
                            DriveService.Scope.DriveFile,
                        }
                    );

                    CreateDriveService(uc);
                    break;

                case StorageMethod.Sftp:
                    if (string.IsNullOrWhiteSpace(Config.SftpHost) || string.IsNullOrWhiteSpace(Config.SftpUsername))
                    {
                        sw.Stop();
                        Config.SftpHost = Config.SftpHost ?? @"";
                        Config.SftpPort = Config.SftpPort < 1 ? (ushort)22 : Config.SftpPort;
                        Config.SftpUsername = Config.SftpUsername ?? @"";
                        Config.SftpPassphrase = Config.SftpPassphrase ?? @"";
                        Config.SftpPrivateKey = Config.SftpPrivateKey ?? @"";
                        Config.SftpTrustedHost = Config.SftpTrustedHost ?? @"";
                        Config.SftpBackupDirectoryPath = Config.SftpBackupDirectoryPath ?? @"";
                        SaveConfig();
                        Error(@"No Sftp host/auth details found in the configuration.");
                    }

                    if (!string.IsNullOrEmpty(Config.SftpPrivateKey))
                        SFTP = new SftpClient(Config.SftpHost, Config.SftpPort, Config.SftpUsername, new PrivateKeyFile(Config.SftpPrivateKey, Config.SftpPassphrase ?? string.Empty));
                    else
                        SFTP = new SftpClient(Config.SftpHost, Config.SftpPort, Config.SftpUsername, Config.SftpPassphrase ?? string.Empty);

                    using (ManualResetEvent mre = new ManualResetEvent(false))
                    {
                        if (!string.IsNullOrWhiteSpace(Config.SftpTrustedHost))
                            SFTP.HostKeyReceived += (s, e) =>
                            {
                                string checkString = e.HostKeyName + @"#" + Convert.ToBase64String(e.HostKey) + @"#" + Convert.ToBase64String(e.FingerPrint);
                                e.CanTrust = Config.SftpTrustedHost.SequenceEqual(checkString);
                                mre.Set();
                            };
                        else
                            mre.Set();

                        try
                        {
                            SFTP.Connect();
                        } catch (SshConnectionException)
                        {
                            Error(@"Error during SFTP connect, it's possible the server key changed.");
                        }

                        mre.WaitOne();
                    }
                    break;
            }

            GetBackupStorage();

            Log(@"Database backup...");

            using (Stream s = CreateMySqlDump())
            using (Stream g = GZipEncodeStream(s))
            {
                object f = Upload(DatabaseDumpName, @"application/sql+gzip", g);

                switch (f)
                {
                    case GFile fgf:
                        Log($@"MySQL dump uploaded: {fgf.Name} ({fgf.Id})");
                        break;

                    default:
                        Log($@"MySQL dump uploaded.");
                        break;
                }
            }

            if (Directory.Exists(Config.MisuzuPath))
            {
                Log(@"Filesystem backup...");
                string mszConfig = GetMisuzuConfig();

                if (!File.Exists(mszConfig))
                    Error(@"Could not find Misuzu config.");

                string mszStore = FindMisuzuStorageDir(mszConfig);

                if (!Directory.Exists(mszStore))
                    Error(@"Could not find Misuzu storage directory.");

                string archivePath = CreateMisuzuDataBackup(mszConfig, mszStore);

                using (FileStream fs = File.OpenRead(archivePath))
                {
                    object f = Upload(UserDataName, @"application/zip", fs);

                    switch (f)
                    {
                        case GFile fgf:
                            Log($@"Misuzu data uploaded: {fgf.Name} ({fgf.Id})");
                            break;

                        default:
                            Log($@"Misuzu data uploaded.");
                            break;
                    }
                }

                File.Delete(archivePath);
            }

            SaveConfig();
            sw.Stop();
            Log($@"Done! Took {sw.Elapsed}.");

#if DEBUG
            Console.ReadLine();
#endif
        }

        public static void Log(object line)
        {
            if (Headless)
                return;

            if (sw?.IsRunning == true)
            {
                ConsoleColor fg = Console.ForegroundColor;
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.Write(sw.ElapsedMilliseconds.ToString().PadRight(10));
                Console.ForegroundColor = fg;
            }

            Console.WriteLine(line);
        }

        public static void Error(object line, int exit = 0x00DEAD00)
        {
            if (!Headless)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Log(line);
                Console.ResetColor();
            }

#if DEBUG
            Console.ReadLine();
#endif

            Environment.Exit(exit);
        }
        
        public static object Upload(string name, string type, Stream stream)
        {
            Log($@"Uploading '{name}'...");

            switch (BackupStorage)
            {
                case GFile gfile:
                    FilesResource.CreateMediaUpload request = DriveService.Files.Create(new GFile
                    {
                        Name = name,
                        Parents = new List<string> {
                            gfile.Id,
                        },
                    }, stream, type);
                    request.Fields = @"id, name";
                    request.Upload();
                    return request.ResponseBody;

                case string scpName:
                    SFTP.UploadFile(stream, scpName + @"/" + name);
                    break;
            }

            return null;
        }

        public static string GetMisuzuConfig()
        {
            return Path.Combine(Config.MisuzuPath, @"config/config.ini");
        }

        public static string FindMisuzuStorageDir(string config)
        {
            Log(@"Finding storage directory...");

            string[] configLines = File.ReadAllLines(config);
            bool storageSectionFound = false;
            string path = string.Empty;

            foreach (string line in configLines)
            {
                if (!string.IsNullOrEmpty(path))
                    break;
                if (line.StartsWith('['))
                    storageSectionFound = line == @"[Storage]";
                if (!storageSectionFound)
                    continue;

                string[] split = line.Split('=', StringSplitOptions.RemoveEmptyEntries);

                if (split.Length < 2 || split[0] != @"path")
                    continue;

                path = string.Join('=', split.Skip(1));
                break;
            }

            if (string.IsNullOrEmpty(path))
                path = Path.Combine(Config.MisuzuPath, @"store");

            return path;
        }

        public static string CreateMisuzuDataBackup(string configPath, string storePath)
        {
            Log(@"Creating Zip archive containing non-volatile Misuzu data...");

            string tmpName = Path.GetTempFileName();

            using (FileStream fs = File.OpenWrite(tmpName))
            using (ZipArchive za = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                za.CreateEntryFromFile(configPath, @"config/config.ini", CompressionLevel.Optimal);

                string[] storeFiles = Directory.GetFiles(storePath, @"*", SearchOption.AllDirectories);

                foreach (string file in storeFiles)
                    za.CreateEntryFromFile(
                        file,
                        @"store/" + file.Replace(storePath, string.Empty).WindowsToUnixPath().Trim('/'),
                        CompressionLevel.Optimal
                    );
            }

            return tmpName;
        }

        public static Stream CreateMySqlDump()
        {
            Log(@"Dumping MySQL Databases...");
            string tmpFile = Path.GetTempFileName();

            using (FileStream fs = File.Open(tmpFile, FileMode.Open, FileAccess.ReadWrite))
            using (StreamWriter sw = new StreamWriter(fs))
            {
                sw.WriteLine(@"[client]");
                sw.WriteLine($@"user={Config.MySqlUser}");
                sw.WriteLine($@"password={Config.MySqlPass}");
                sw.WriteLine(@"default-character-set=utf8");
            }

            Process p = Process.Start(new ProcessStartInfo
            {
                FileName = IsWindows ? Config.MySqlDumpPathWindows : Config.MySqlDumpPath,
                RedirectStandardError = false,
                RedirectStandardInput = false,
                RedirectStandardOutput = true,
                Arguments = $@"--defaults-file={tmpFile} --add-locks -l --order-by-primary -B {Config.MySqlDatabases}",
                UseShellExecute = false,
                CreateNoWindow = true,
            });

            int read;
            byte[] buffer = new byte[1024];
            MemoryStream ms = new MemoryStream();

            while ((read = p.StandardOutput.BaseStream.Read(buffer, 0, buffer.Length)) > 0)
                ms.Write(buffer, 0, read);

            p.WaitForExit();
            File.Delete(tmpFile);
            ms.Seek(0, SeekOrigin.Begin);

            return ms;
        }

        public static Stream GZipEncodeStream(Stream input)
        {
            Log(@"Compressing stream...");
            MemoryStream output = new MemoryStream();

            using (GZipStream gz = new GZipStream(output, CompressionLevel.Optimal, true))
                input.CopyTo(gz);

            output.Seek(0, SeekOrigin.Begin);
            return output;
        }
        
        public static UserCredential GoogleAuthenticate(ClientSecrets cs, string[] scopes)
        {
            Log(@"Authenticating with Google...");
            return GoogleWebAuthorizationBroker.AuthorizeAsync(
                cs,
                scopes,
                @"user",
                CancellationToken.None,
                new GoogleDatastore(Config),
                new PromptCodeReceiver()
            ).Result;
        }

        public static void CreateDriveService(UserCredential uc)
        {
            Log(@"Creating Google Drive service...");
            DriveService = new DriveService(new BaseClientService.Initializer()
            {
                HttpClientInitializer = uc,
                ApplicationName = @"Flashii Backup Manager",
            });
        }

        public static void GetBackupStorage(string name = null)
        {
            switch (Config.StorageMethod)
            {
                case StorageMethod.GoogleDrive:
                    name = name ?? Config.GoogleBackupDirectory;
                    Log(@"Getting backup folder...");
                    FilesResource.ListRequest lr = DriveService.Files.List();
                    lr.Q = $@"name = '{name}' and mimeType = '{FOLDER_MIME}'";
                    lr.PageSize = 1;
                    lr.Fields = @"files(id)";
                    GFile backupFolder = lr.Execute().Files.FirstOrDefault();

                    if (backupFolder == null)
                    {
                        Log(@"Backup folder doesn't exist yet, creating it...");
                        FilesResource.CreateRequest dcr = DriveService.Files.Create(new GFile
                        {
                            Name = name,
                            MimeType = FOLDER_MIME,
                        });
                        dcr.Fields = @"id";
                        backupFolder = dcr.Execute();
                    }

                    BackupStorage = backupFolder;
                    break;

                case StorageMethod.Sftp:
                    string directory = (BackupStorage = name ?? Config.SftpBackupDirectoryPath) as string;
                    try
                    {
                        SFTP.ListDirectory(directory);
                    } catch (SftpPathNotFoundException)
                    {
                        SFTP.CreateDirectory(directory);
                    }
                    break;
            }
        }
    }
}