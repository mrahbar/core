﻿using DbUp;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Net.Http;
using System.Reflection;

namespace Bit.Setup
{
    public class Program
    {
        private static Context _context;

        public static void Main(string[] args)
        {
            Console.WriteLine();
            _context = new Context
            {
                Args = args
            };
            ParseParameters();

            if(_context.Parameters.ContainsKey("os"))
            {
                _context.HostOS = _context.Parameters["os"];
            }
            if(_context.Parameters.ContainsKey("corev"))
            {
                _context.CoreVersion = _context.Parameters["corev"];
            }
            if(_context.Parameters.ContainsKey("webv"))
            {
                _context.WebVersion = _context.Parameters["webv"];
            }

            if(_context.Parameters.ContainsKey("install"))
            {
                Install();
            }
            else if(_context.Parameters.ContainsKey("update"))
            {
                Update();
            }
            else if(_context.Parameters.ContainsKey("printenv"))
            {
                PrintEnvironment();
            }
            else
            {
                Console.WriteLine("No top-level command detected. Exiting...");
            }
        }

        private static void Install()
        {
            if(_context.Parameters.ContainsKey("letsencrypt"))
            {
                _context.Config.SslManagedLetsEncrypt =
                    _context.Parameters["letsencrypt"].ToLowerInvariant() == "y";
            }
            if(_context.Parameters.ContainsKey("domain"))
            {
                _context.Install.Domain = _context.Parameters["domain"].ToLowerInvariant();
            }

            if(!ValidateInstallation())
            {
                return;
            }

            var certBuilder = new CertBuilder(_context);
            certBuilder.BuildForInstall();

            // Set the URL
            _context.Config.Url = string.Format("http{0}://{1}",
                _context.Config.Ssl ? "s" : string.Empty, _context.Install.Domain);

            var nginxBuilder = new NginxConfigBuilder(_context);
            nginxBuilder.BuildForInstaller();

            var environmentFileBuilder = new EnvironmentFileBuilder(_context);
            environmentFileBuilder.BuildForInstaller();

            var appIdBuilder = new AppIdBuilder(_context);
            appIdBuilder.Build();

            var dockerComposeBuilder = new DockerComposeBuilder(_context);
            dockerComposeBuilder.BuildForInstaller();

            _context.SaveConfiguration();

            Console.WriteLine("\nInstallation complete");

            Console.WriteLine("\nIf you need to make additional configuration changes, you can modify\n" +
                "the settings in `{0}` and then run:\n{1}",
                _context.HostOS == "win" ? ".\\bwdata\\config.yml" : "./bwdata/config.yml",
                _context.HostOS == "win" ? "`.\\bitwarden.ps1 -rebuild` or `.\\bitwarden.ps1 -update`" :
                    "`./bitwarden.sh rebuild` or `./bitwarden.sh update`");

            Console.WriteLine("\nNext steps, run:");
            if(_context.HostOS == "win")
            {
                Console.WriteLine("`.\\bitwarden.ps1 -start` and then `.\\bitwarden.ps1 -updatedb`");
            }
            else
            {
                Console.WriteLine("`./bitwarden.sh start` and then `./bitwarden.sh updatedb`");
            }
            Console.WriteLine(string.Empty);
        }

        private static void Update()
        {
            if(_context.Parameters.ContainsKey("db"))
            {
                MigrateDatabase();
            }
            else
            {
                RebuildConfigs();
            }
        }

        private static void PrintEnvironment()
        {
            _context.LoadConfiguration();
            Console.WriteLine("\nBitwarden is up and running!");
            Console.WriteLine("===================================================");
            Console.WriteLine("\nvisit {0}", _context.Config.Url);
            Console.Write("to update, run ");
            if(_context.HostOS == "win")
            {
                Console.Write("`.\\bitwarden.ps1 -updateself` and then `.\\bitwarden.ps1 -update`");
            }
            else
            {
                Console.Write("`./bitwarden.sh updateself` and then `./bitwarden.sh update`");
            }
            Console.WriteLine("\n");
        }

        private static void MigrateDatabase(int attempt = 1)
        {
            try
            {
                Console.WriteLine("Migrating database.");

                var dbPass = Helpers.GetValueFronEnvFile("mssql", "SA_PASSWORD");
                var masterConnectionString = Helpers.MakeSqlConnectionString(
                    "mssql", "master", "sa", dbPass ?? string.Empty);
                var vaultConnectionString = Helpers.MakeSqlConnectionString(
                    "mssql", "vault", "sa", dbPass ?? string.Empty);

                using(var connection = new SqlConnection(masterConnectionString))
                {
                    var command = new SqlCommand(
                        "IF ((SELECT COUNT(1) FROM sys.databases WHERE [name] = 'vault') = 0) " +
                        "CREATE DATABASE [vault];", connection);
                    command.Connection.Open();
                    command.ExecuteNonQuery();
                    command.CommandText = "IF ((SELECT DATABASEPROPERTYEX([name], 'IsAutoClose') " +
                        "FROM sys.databases WHERE [name] = 'vault') = 1) " +
                        "ALTER DATABASE [vault] SET AUTO_CLOSE OFF;";
                    command.ExecuteNonQuery();
                }

                var upgrader = DeployChanges.To
                    .SqlDatabase(vaultConnectionString)
                    .JournalToSqlTable("dbo", "Migration")
                    .WithScriptsAndCodeEmbeddedInAssembly(Assembly.GetExecutingAssembly(),
                        s => s.Contains($".DbScripts.") && !s.Contains(".Archive."))
                    .WithTransaction()
                    .WithExecutionTimeout(new TimeSpan(0, 5, 0))
                    .LogToConsole()
                    .Build();

                var result = upgrader.PerformUpgrade();
                if(result.Successful)
                {
                    Console.WriteLine("Migration successful.");
                }
                else
                {
                    Console.WriteLine("Migration failed.");
                }
            }
            catch(SqlException e)
            {
                if(e.Message.Contains("Server is in script upgrade mode") && attempt < 10)
                {
                    var nextAttempt = attempt + 1;
                    Console.WriteLine("Database is in script upgrade mode. " +
                        "Trying again (attempt #{0})...", nextAttempt);
                    System.Threading.Thread.Sleep(20000);
                    MigrateDatabase(nextAttempt);
                    return;
                }

                throw e;
            }
        }

        private static bool ValidateInstallation()
        {
            var installationId = Helpers.ReadInput("Enter your installation id (get at https://bitwarden.com/host)");
            if(!Guid.TryParse(installationId.Trim(), out var installationidGuid))
            {
                Console.WriteLine("Invalid installation id.");
                return false;
            }

            _context.Install.InstallationId = installationidGuid;
            _context.Install.InstallationKey = Helpers.ReadInput("Enter your installation key");

            try
            {
                var response = new HttpClient().GetAsync("https://api.bitwarden.com/installations/" +
                    _context.Install.InstallationId).GetAwaiter().GetResult();

                if(!response.IsSuccessStatusCode)
                {
                    if(response.StatusCode == System.Net.HttpStatusCode.NotFound)
                    {
                        Console.WriteLine("Invalid installation id.");
                    }
                    else
                    {
                        Console.WriteLine("Unable to validate installation id.");
                    }

                    return false;
                }

                var resultString = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                var result = JsonConvert.DeserializeObject<dynamic>(resultString);
                if(!(bool)result.Enabled)
                {
                    Console.WriteLine("Installation id has been disabled.");
                    return false;
                }

                return true;
            }
            catch
            {
                Console.WriteLine("Unable to validate installation id. Problem contacting Bitwarden server.");
                return false;
            }
        }

        private static void RebuildConfigs()
        {
            _context.LoadConfiguration();

            var environmentFileBuilder = new EnvironmentFileBuilder(_context);
            environmentFileBuilder.BuildForUpdater();

            var nginxBuilder = new NginxConfigBuilder(_context);
            nginxBuilder.BuildForUpdater();

            var appIdBuilder = new AppIdBuilder(_context);
            appIdBuilder.Build();

            var dockerComposeBuilder = new DockerComposeBuilder(_context);
            dockerComposeBuilder.BuildForUpdater();

            _context.SaveConfiguration();
            Console.WriteLine(string.Empty);
        }

        private static void ParseParameters()
        {
            _context.Parameters = new Dictionary<string, string>();
            for(var i = 0; i < _context.Args.Length; i = i + 2)
            {
                if(!_context.Args[i].StartsWith("-"))
                {
                    continue;
                }

                _context.Parameters.Add(_context.Args[i].Substring(1), _context.Args[i + 1]);
            }
        }
    }
}
