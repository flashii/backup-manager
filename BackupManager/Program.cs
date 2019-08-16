using Renci.SshNet;
using Renci.SshNet.Common;
using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Xml;
using System.Xml.Serialization;

namespace BackupManager
{
    public static class Program
    {
        public readonly static Stopwatch sw = new Stopwatch();

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

                case StorageMethod.FileSystem:
                    if (!Directory.Exists(Config.FileSystemPath))
                        Directory.CreateDirectory(Config.FileSystemPath);
                    break;
            }

            GetBackupStorage();

            Log(@"Database backup...");

            string sqldump = CreateMySqlDump();

            using (Stream s = File.OpenRead(sqldump))
            using (Stream g = GZipEncodeStream(s))
            {
                object f = Upload(DatabaseDumpName, @"application/sql+gzip", g);

                switch (f)
                {
                    default:
                        Log($@"MySQL dump uploaded.");
                        break;
                }
            }

            File.Delete(sqldump);

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
                        default:
                            Log($@"Misuzu data uploaded.");
                            break;
                    }
                }

                File.Delete(archivePath);
            }

            SaveConfig();
            sw.Stop();
            Log($@"Done! Took {sw.Elapsed}.", true);

#if DEBUG
            Console.ReadLine();
#endif
        }

        public static void Log(object line, bool forceSatori = false)
        {
            if (!Headless)
            {
                if (sw?.IsRunning == true)
                {
                    ConsoleColor fg = Console.ForegroundColor;
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.Write(sw.ElapsedMilliseconds.ToString().PadRight(10));
                    Console.ForegroundColor = fg;
                }

                Console.WriteLine(line);
            }

            if (forceSatori || (!Headless && !(Config?.SatoriErrorsOnly ?? true)))
                SatoriBroadcast(line.ToString());
        }

        public static void Error(object line, int exit = 0x00DEAD00)
        {
            if (!Headless)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Log(line);
                Console.ResetColor();
            }

            SatoriBroadcast(line.ToString(), true);

#if DEBUG
            Console.ReadLine();
#endif

            Environment.Exit(exit);
        }
        
        public static object Upload(string name, string type, Stream stream)
        {
            Log($@"Uploading '{name}'...");

            switch (Config.StorageMethod)
            {
                case StorageMethod.Sftp:
                    SFTP.UploadFile(stream, (BackupStorage as string) + @"/" + name);
                    break;

                case StorageMethod.FileSystem:
                    string filename = Path.Combine(BackupStorage as string, name);

                    using (FileStream fs = File.OpenWrite(filename))
                        stream.CopyTo(fs);
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

        public static string CreateMySqlDump()
        {
            Log(@"Dumping MySQL Databases...");
            string sqldefaults = Path.GetTempFileName();

            using (FileStream fs = File.Open(sqldefaults, FileMode.Open, FileAccess.ReadWrite))
            using (StreamWriter sw = new StreamWriter(fs))
            {
                sw.WriteLine(@"[client]");
                sw.WriteLine($@"user={Config.MySqlUser}");
                sw.WriteLine($@"password={Config.MySqlPass}");
                sw.WriteLine(@"default-character-set=utf8mb4");
            }

            string sqldump = Path.GetTempFileName();

            StringBuilder mysqldumpArgs = new StringBuilder();
            mysqldumpArgs.AppendFormat(@"--defaults-file={0} ", sqldefaults);
            mysqldumpArgs.Append(@"--single-transaction ");
            mysqldumpArgs.Append(@"--tz-utc --triggers ");
            mysqldumpArgs.Append(@"--routines --hex-blob ");
            mysqldumpArgs.Append(@"--add-locks --order-by-primary ");
            mysqldumpArgs.AppendFormat(@"--result-file={0} ", sqldump);
            mysqldumpArgs.Append(@"-l -Q -q -B "); // lock, quote names, quick, databases list
            mysqldumpArgs.Append(Config.MySqlDatabases);

#if DEBUG
            Log($@"mysqldump args: {mysqldumpArgs}");
#endif

            Process p = Process.Start(new ProcessStartInfo
            {
                FileName = IsWindows ? Config.MySqlDumpPathWindows : Config.MySqlDumpPath,
                Arguments = mysqldumpArgs.ToString(),
                UseShellExecute = false,
                CreateNoWindow = true,
            });

            p.WaitForExit();

            File.Delete(sqldefaults);

            return sqldump;
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
        
        public static void GetBackupStorage(string name = null)
        {
            switch (Config.StorageMethod)
            {
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

                case StorageMethod.FileSystem:
                    BackupStorage = name ?? Config.FileSystemPath;
                    break;
            }
        }

        public static void SatoriBroadcast(string text, bool error = false)
        {
#if DEBUG
            return;
#endif
            if (string.IsNullOrEmpty(text)
                || Config == null
                || string.IsNullOrWhiteSpace(Config.SatoriHost)
                || string.IsNullOrWhiteSpace(Config.SatoriSecret)
                || Config.SatoriPort < 1)
                return;

            IPAddress ip = null;

            try
            {
                ip = IPAddress.Parse(Config.SatoriHost);
            }
            catch
            {
                try
                {
                    ip = Dns.GetHostAddresses(Config.SatoriHost).FirstOrDefault();
                }
                catch
                {
                    ip = null;
                }
            }

            if (ip == null)
                return;

            EndPoint endPoint = new IPEndPoint(ip, Config.SatoriPort);

            StringBuilder textBuilder = new StringBuilder();
            textBuilder.Append(@"[b]Backup System[/b]: ");

            if (error)
                textBuilder.Append(@"[color=red]");

            textBuilder.Append(text);

            if (error)
                textBuilder.Append(@"[/color]");

            text = textBuilder.ToString();

            StringBuilder messageBuilder = new StringBuilder();

            using (HMACSHA256 hmac = new HMACSHA256(Encoding.UTF8.GetBytes(Config.SatoriSecret)))
            {
                byte[] hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(text));

                foreach (byte b in hash)
                    messageBuilder.AppendFormat(@"{0:x2}", b);
            }

            messageBuilder.Append(text);
            string message = messageBuilder.ToString();
            byte[] messageBytes = new byte[Encoding.UTF8.GetByteCount(message) + 2];
            messageBytes[0] = messageBytes[messageBytes.Length - 1] = 0x0F;
            Encoding.UTF8.GetBytes(message).CopyTo(messageBytes, 1);

            using (Socket sock = new Socket(endPoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp))
            {
                sock.NoDelay = sock.Blocking = true;
                sock.Connect(endPoint);
                sock.Send(messageBytes);
            }
        }
    }
}
