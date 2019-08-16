namespace BackupManager
{
    public class Config
    {
        public StorageMethod StorageMethod { get; set; } = StorageMethod.Sftp;
        
        public string SftpHost { get; set; }
        public ushort SftpPort { get; set; }
        public string SftpUsername { get; set; }
        public string SftpPassphrase { get; set; }
        public string SftpPrivateKey { get; set; }
        public string SftpBackupDirectoryPath { get; set; }
        public string SftpTrustedHost { get; set; }

        public string FileSystemPath { get; set; } = @"backups";

        public string MySqlDumpPathWindows { get; set; } = @"C:\Program Files\MariaDB 10.3\bin\mysqldump.exe";
        public string MySqlDumpPath { get; set; } = @"mysqldump";
        public string MySqlHost { get; set; } = @"localhost";
        public string MySqlUser { get; set; }
        public string MySqlPass { get; set; }
        public string MySqlDatabases { get; set; } = @"misuzu";

        public string MisuzuPath { get; set; }

        public string SatoriHost { get; set; }
        public ushort SatoriPort { get; set; }
        public string SatoriSecret { get; set; }
        public bool SatoriErrorsOnly { get; set; } = true;
    }
}
