using MySql.Data.MySqlClient;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Serialization;

namespace BackupManager {
    public static class Program {
        public readonly static Stopwatch sw = new Stopwatch();

        private const string CONFIG_NAME = @"FlashiiBackupManager.v1.xml";

        public static bool IsWindows
            => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

        public readonly static DateTimeOffset Startup = DateTimeOffset.UtcNow;

        public static string Basename
            => $@"{Environment.MachineName} {Startup.Year:0000}-{Startup.Month:00}-{Startup.Day:00} {Startup.Hour:00}{Startup.Minute:00}{Startup.Second:00}";
        public static string BackupName
            => $@"{Basename}.zip";

        private static Config Config;
        private readonly static string ConfigPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Personal),
            CONFIG_NAME
        );

        public static string BackupStore => string.IsNullOrWhiteSpace(Config.FileSystemPathV2)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Personal), @"Backups")
            : Config.FileSystemPathV2;

        public static bool Headless;

        public static string WindowsToUnixPath(this string path) {
            return IsWindows ? path.Replace('\\', '/') : path;
        }

        public static Stream ToXml(this object obj, bool pretty = false) {
            MemoryStream ms = new MemoryStream();
            XmlSerializer xs = new XmlSerializer(obj.GetType());

            using (XmlWriter xw = XmlWriter.Create(ms, new XmlWriterSettings { Indent = pretty }))
                xs.Serialize(xw, obj);

            ms.Seek(0, SeekOrigin.Begin);
            return ms;
        }

        public static T FromXml<T>(Stream xml) {
            if (xml.CanSeek)
                xml.Seek(0, SeekOrigin.Begin);

            XmlSerializer xs = new XmlSerializer(typeof(T));
            return (T)xs.Deserialize(xml);
        }

        public static void SaveConfig() {
            Log(@"Saving configuration...");
            using (FileStream fs = new FileStream(ConfigPath, FileMode.Create, FileAccess.Write))
            using (Stream cs = Config.ToXml(true))
                cs.CopyTo(fs);
        }

        public static void LoadConfig() {
            Log(@"Loading configuration...");
            using (FileStream fs = File.OpenRead(ConfigPath))
                Config = FromXml<Config>(fs);
        }

        public static void Main(string[] args) {
            Headless = args.Contains(@"-cron") || args.Contains(@"-headless");

            Log(@"Flashii Backup Manager");
            sw.Start();

            if (!File.Exists(ConfigPath)) {
                Config = new Config();
                SaveConfig();
                Error(@"No configuration file exists, created a blank one. Be sure to fill it out properly.");
            }

            LoadConfig();

            if (!Directory.Exists(BackupStore))
                Directory.CreateDirectory(BackupStore);

            Log(@"Fetching database list...");

            MySqlConnectionStringBuilder connStr = new MySqlConnectionStringBuilder {
                Server = Config.MySqlHost,
                UserID = Config.MySqlUser,
                Password = Config.MySqlPass,
                CharacterSet = @"utf8mb4",
            };

            List<string> databases = new List<string>();
            string[] exclude = Config.MySqlExcludeDatabases.Split(' ');

            using (MySqlConnection conn = new MySqlConnection(connStr.ToString())) {
                conn.Open();

                using (MySqlCommand comm = new MySqlCommand(@"SHOW DATABASES;", conn))
                using (MySqlDataReader read = comm.ExecuteReader()) {
                    while (read.Read()) {
                        string database = read.GetString(0);

                        if (string.IsNullOrEmpty(database) || exclude.Contains(database))
                            continue;

                        databases.Add(database);
                    }
                }
            }

            Log(@"Creating backup archive...");

            string archivePath = Path.GetTempFileName();

            using (FileStream fs = File.OpenWrite(archivePath))
            using (ZipArchive archive = new ZipArchive(fs, ZipArchiveMode.Create)) {
                Log(@"Database backup...");

                string sqldefaults = Path.GetTempFileName();

                using (FileStream sqlConfFs = File.Open(sqldefaults, FileMode.Open, FileAccess.ReadWrite))
                using (StreamWriter sw = new StreamWriter(sqlConfFs)) {
                    sw.WriteLine(@"[client]");
                    sw.WriteLine($@"user={Config.MySqlUser}");
                    sw.WriteLine($@"password={Config.MySqlPass}");
                    sw.WriteLine(@"default-character-set=utf8mb4");
                }

                foreach (string database in databases)
                    CreateDbDump(archive, sqldefaults, database);

                Log($@"MariaDB dump done.");
                File.Delete(sqldefaults);

                if (Directory.Exists(Config.MisuzuPath)) {
                    Log(@"Filesystem backup...");
                    string mszConfig = Path.Combine(Config.MisuzuPath, @"config/config.ini");

                    if (!File.Exists(mszConfig))
                        Error(@"Could not find Misuzu config.");

                    string mszStore = Path.Combine(Config.MisuzuPath, @"store");

                    if (!Directory.Exists(mszStore))
                        Error(@"Could not find Misuzu storage directory.");

                    CreateMisuzuDataBackup(archive, mszConfig, mszStore);
                }
            }

            string targetPath = Path.Combine(BackupStore, BackupName);
            File.Move(archivePath, targetPath);
            Log($@"Moved backup archive to {targetPath}");

            SaveConfig();
            sw.Stop();
            Log($@"Done! Took {sw.Elapsed}.", true);

#if DEBUG
            Console.ReadLine();
#endif
        }

        public static void Log(object line, bool forceSatori = false) {
            if (!Headless) {
                if (sw?.IsRunning == true) {
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

        public static void Error(object line, int exit = 0x00DEAD00) {
            if (!Headless) {
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

        public static void CreateMisuzuDataBackup(ZipArchive archive, string configPath, string storePath) {
            Log(@"Storing non-volatile Misuzu data...");

            archive.CreateEntryFromFile(configPath, @"misuzu/config/config.ini", CompressionLevel.Optimal);

            string[] storeFiles = Directory.GetFiles(storePath, @"*", SearchOption.AllDirectories);

            foreach (string file in storeFiles)
                archive.CreateEntryFromFile(
                    file,
                    @"misuzu/store/" + file.Replace(storePath, string.Empty).WindowsToUnixPath().Trim('/'),
                    CompressionLevel.Optimal
                );
        }

        public static void CreateDbDump(ZipArchive archive, string defaults, string database) {
            Log($@"Dumping {database}...");

            string sqldump = Path.GetTempFileName();

            StringBuilder mysqldumpArgs = new StringBuilder();
            mysqldumpArgs.AppendFormat(@"--defaults-file={0} ", defaults);
            mysqldumpArgs.Append(@"--single-transaction ");
            mysqldumpArgs.Append(@"--tz-utc --triggers ");
            mysqldumpArgs.Append(@"--routines --hex-blob ");
            mysqldumpArgs.Append(@"--add-locks --order-by-primary ");
            mysqldumpArgs.Append(@"--skip-lock-tables "); // might regret this, we'll find out someday
            mysqldumpArgs.AppendFormat(@"--result-file={0} ", sqldump);
            mysqldumpArgs.Append(@"-Q -q -B "); // lock, quote names, quick, database list
            mysqldumpArgs.Append(database);

#if DEBUG
            Log($@"mysqldump args: {mysqldumpArgs}");
#endif

            Process p = Process.Start(new ProcessStartInfo {
                FileName = IsWindows ? Config.MySqlDumpPathWindows : Config.MySqlDumpPath,
                Arguments = mysqldumpArgs.ToString(),
                UseShellExecute = false,
                CreateNoWindow = true,
            });

            p.WaitForExit();

            archive.CreateEntryFromFile(sqldump, $@"mariadb/{database}.sql", CompressionLevel.Optimal);

            File.Delete(sqldump);
        }

        public static void SatoriBroadcast(string text, bool error = false) {
            if (string.IsNullOrEmpty(text)
                || Config == null
                || string.IsNullOrWhiteSpace(Config.SatoriHost)
                || string.IsNullOrWhiteSpace(Config.SatoriSecret)
                || Config.SatoriPort < 1)
                return;

            IPAddress ip = null;

            try {
                ip = IPAddress.Parse(Config.SatoriHost);
            } catch {
                try {
                    // forcing IPv4 here, it seems to explode with IPv6 and i don't really want to figure out why
                    ip = Dns.GetHostAddresses(Config.SatoriHost).FirstOrDefault(x => x.AddressFamily == AddressFamily.InterNetwork);
                } catch {
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

            using (HMACSHA256 hmac = new HMACSHA256(Encoding.UTF8.GetBytes(Config.SatoriSecret))) {
                byte[] hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(text));

                foreach (byte b in hash)
                    messageBuilder.AppendFormat(@"{0:x2}", b);
            }

            messageBuilder.Append(text);
            string message = messageBuilder.ToString();
            byte[] messageBytes = new byte[Encoding.UTF8.GetByteCount(message) + 2];
            messageBytes[0] = messageBytes[messageBytes.Length - 1] = 0x0F;
            Encoding.UTF8.GetBytes(message).CopyTo(messageBytes, 1);

            using (Socket sock = new Socket(endPoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp)) {
                sock.NoDelay = sock.Blocking = true;
                sock.Connect(endPoint);
                sock.Send(messageBytes);
            }
        }
    }
}
