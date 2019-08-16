cwd="$(pwd)"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
cd "$(dirname "$0")"
dotnet run --project BackupManager -c Release -f netcoreapp2.2 -- -cron
cd "$(cwd)"
