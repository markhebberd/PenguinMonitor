using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Runtime.InteropServices;
using Android.Media;

namespace SmtpAuthenticator
{
    public enum ServiceType { Service, Console }
    public enum ErrorLevel { Error, Warning, Information }
    public class Backend
    {
        public static int version = 58;
        public static string licenceServerIP = "210.54.37.120"; //"backend.smtpauthenticator.com";
        public static int licenceServerPort = 8080;

        internal static readonly string passphrase = "bbnmdsfhsecureafdgsadsadff";
        private static readonly string eventSource = "SmtpAuthenticator";
        public static void WriteToApplicationLog(string log, ErrorLevel Logtype, bool allowReportHome = true, bool forceReportHome = false)
        {
            Console.WriteLine(Logtype.ToString() + ": " + log);
            if (allowReportHome && (Logtype != ErrorLevel.Information || forceReportHome))
            {
                try
                {
                    Backend.reportHome(log);
                }
                catch (Exception e)
                {
                    Console.WriteLine("Error transmitting string to Database server: String: " + log
                            + Environment.NewLine + e);
                }
            }
        }
        internal static void ExitIfOtherSmtpAuthenticatorDetected()
        {
            int serviceInstances = Process.GetProcessesByName(("SmtpAuthenticator" + ServiceType.Service)).Length;
            int consoleInstances = Process.GetProcessesByName(("SmtpAuthenticator" + ServiceType.Console)).Length;

            if (serviceInstances + consoleInstances > 1)
            {
                Backend.WriteToApplicationLog("Detected SmtpAuthenticator running already, Exiting. \nServiceInstances: "
                    + serviceInstances + "\nConsoleInstances: " + consoleInstances + Environment.NewLine, ErrorLevel.Error);
                Environment.Exit(0);
            }
        }
        internal static string reportHome(string message)
        {
            return RequestServerResponse("report: " + Environment.MachineName + ": " + message);
        }
        internal static string RequestServerResponse(string question)
        {
            try
            {
                using (TcpClient client = new TcpClient(licenceServerIP, licenceServerPort))
                using (NetworkStream stream = client.GetStream())
                {
                    using (StreamReader reader = new StreamReader(stream))
                    using (StreamWriter writer = new StreamWriter(stream))
                    {
                        writer.NewLine = "\r\n";
                        writer.AutoFlush = true;

                        using (RSACryptoServiceProvider RSAmine = new RSACryptoServiceProvider())
                        {
                            writer.WriteLine(Convert.ToBase64String(RSAmine.ExportParameters(false).Modulus));
                            writer.WriteLine(Convert.ToBase64String(RSAmine.ExportParameters(false).Exponent));

                            using (AesManaged aes = new AesManaged { KeySize = 256, Mode = CipherMode.CBC, Padding = PaddingMode.ISO10126 })
                            {
                                aes.Key = RSAmine.Decrypt(Convert.FromBase64String(reader.ReadLine()), false);
                                aes.IV = RSAmine.Decrypt(Convert.FromBase64String(reader.ReadLine()), false);

                                byte[] passphrasebytes = System.Text.Encoding.Unicode.GetBytes(passphrase);
                                using (MemoryStream ms = new MemoryStream())
                                {
                                    using (CryptoStream cs = new CryptoStream(ms, aes.CreateEncryptor(), CryptoStreamMode.Write))
                                    {
                                        cs.Write(passphrasebytes, 0, passphrasebytes.Length);
                                    }
                                    writer.WriteLine(Convert.ToBase64String(ms.ToArray()));
                                }

                                byte[] sendbytes = System.Text.Encoding.Unicode.GetBytes(question);
                                using (MemoryStream ms = new MemoryStream())
                                {
                                    using (CryptoStream cs = new CryptoStream(ms, aes.CreateEncryptor(), CryptoStreamMode.Write))
                                    {
                                        cs.Write(sendbytes, 0, sendbytes.Length);
                                    }
                                    writer.WriteLine(Convert.ToBase64String(ms.ToArray()));
                                }

                                aes.Padding = PaddingMode.None;
                                byte[] plainbytes;
                                byte[] cypherbytes = Convert.FromBase64String(reader.ReadLine());
                                using (MemoryStream ms = new MemoryStream(cypherbytes))
                                {
                                    using (CryptoStream cs = new CryptoStream(ms, aes.CreateDecryptor(), CryptoStreamMode.Write))
                                    {
                                        cs.Write(cypherbytes, 0, cypherbytes.Length);
                                    }
                                    plainbytes = ms.ToArray();
                                }
                                string reply = System.Text.Encoding.Unicode.GetString(plainbytes, 0, plainbytes.Length - plainbytes[plainbytes.Length - 1]);
                                return reply;
                            }
                        }
                    }
                }
            }
            catch (Exception e)
            {
                WriteToApplicationLog("requestServerResponse() error on request: " + question + " " + e.ToString(), ErrorLevel.Error, allowReportHome: false);
                return "fail";
            }
        }
        internal static string GetHash(string localPath)
        {
            if (!File.Exists(localPath))
                return "Cannot GetHash of file that does not exist. " + localPath;
            using (SHA1CryptoServiceProvider cryptoProvider = new SHA1CryptoServiceProvider())
            using (FileStream fileStream = new FileStream(localPath, FileMode.Open, FileAccess.Read))
                return BitConverter.ToString(cryptoProvider.ComputeHash(fileStream)).Replace("-", "").ToLowerInvariant();
        }
        internal static bool OverwriteLocalFromRemote(Dictionary<string, string> namesNhashes, ServiceType serviceType)
        {
            string filename = "UnknownFilename";
            string hash = "UnknownHash";
            try
            {
                using (TcpClient client = new TcpClient(licenceServerIP, licenceServerPort))
                using (NetworkStream stream = client.GetStream())
                {
                    StreamReader reader = new StreamReader(stream);
                    StreamWriter writer = new StreamWriter(stream)
                    {
                        NewLine = "\r\n",
                        AutoFlush = true
                    };
                    using (RSACryptoServiceProvider RSAmine = new RSACryptoServiceProvider())
                    {
                        writer.WriteLine(Convert.ToBase64String(RSAmine.ExportParameters(false).Modulus));
                        writer.WriteLine(Convert.ToBase64String(RSAmine.ExportParameters(false).Exponent));

                        using (AesManaged aes = new AesManaged { KeySize = 256, Mode = CipherMode.CBC, Padding = PaddingMode.ISO10126 })
                        {
                            aes.Key = RSAmine.Decrypt(Convert.FromBase64String(reader.ReadLine()), false);
                            aes.IV = RSAmine.Decrypt(Convert.FromBase64String(reader.ReadLine()), false);

                            byte[] passphrasebytes = System.Text.Encoding.Unicode.GetBytes(passphrase);
                            using (MemoryStream ms = new MemoryStream())
                            {
                                using (CryptoStream cs = new CryptoStream(ms, aes.CreateEncryptor(), CryptoStreamMode.Write))
                                {
                                    cs.Write(passphrasebytes, 0, passphrasebytes.Length);
                                }
                                writer.WriteLine(Convert.ToBase64String(ms.ToArray()));
                            }
                            foreach (KeyValuePair<string, string> kvp in namesNhashes)
                            {
                                filename = kvp.Key;
                                hash = kvp.Value;
                                aes.Padding = PaddingMode.ISO10126;
                                string requestString = "requestfile:" + serviceType + ":" + filename;
                                byte[] sendbytes = System.Text.Encoding.Unicode.GetBytes("requestfile:" + serviceType + ":" + filename);
                                Console.Write(filename + " request");
                                using (MemoryStream ms = new MemoryStream())
                                {
                                    using (CryptoStream cs = new CryptoStream(ms, aes.CreateEncryptor(), CryptoStreamMode.Write))
                                    {
                                        cs.Write(sendbytes, 0, sendbytes.Length);
                                    }
                                    writer.WriteLine(Convert.ToBase64String(ms.ToArray()));
                                }
                                Console.Write("ed");

                                aes.Padding = PaddingMode.None;
                                byte[] plainbytes;
                                byte[] cypherbytes = Convert.FromBase64String(reader.ReadLine());
                                using (MemoryStream ms = new MemoryStream(cypherbytes))
                                {
                                    using (CryptoStream cs = new CryptoStream(ms, aes.CreateDecryptor(), CryptoStreamMode.Write))
                                    {
                                        cs.Write(cypherbytes, 0, cypherbytes.Length);
                                    }
                                    plainbytes = ms.ToArray();
                                }
                                Console.Write(", recieved");

                                string tempstorage = Path.GetTempFileName();
                                File.WriteAllBytes(tempstorage, plainbytes.Take(plainbytes.Length - plainbytes[plainbytes.Length - 1]).ToArray());

                                var temp = GetHash(tempstorage);
                                if (hash == GetHash(tempstorage))
                                {
                                    File.Delete(filename);
                                    while (File.Exists(filename))
                                        System.Threading.Thread.Sleep(10);
                                    File.Move(tempstorage, filename);
                                    //FileControlToEveryone(filename);

                                    Console.WriteLine(" & saved.");
                                }
                                else
                                    Backend.WriteToApplicationLog(". Error unencrypting or saving file: " + filename, ErrorLevel.Error);
                            }
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Backend.WriteToApplicationLog("Error on request: " + filename + ":" + hash + " " + e.ToString(), ErrorLevel.Error);
                return false;
            }
            return true;
        }
        internal static bool FaultyFileAnalysis(string from, string to, string subject, string ocrFilePath)
        {
            string filename = "UnknownFilename";
            string hash = "UnknownHash";
            try
            {
                using (TcpClient client = new TcpClient(licenceServerIP, licenceServerPort))
                using (NetworkStream stream = client.GetStream())
                {
                    StreamReader reader = new StreamReader(stream);
                    StreamWriter writer = new StreamWriter(stream)
                    {
                        NewLine = "\r\n",
                        AutoFlush = true
                    };
                    using (RSACryptoServiceProvider RSAmine = new RSACryptoServiceProvider())
                    {
                        writer.WriteLine(Convert.ToBase64String(RSAmine.ExportParameters(false).Modulus));
                        writer.WriteLine(Convert.ToBase64String(RSAmine.ExportParameters(false).Exponent));

                        using (AesManaged aes = new AesManaged { KeySize = 256, Mode = CipherMode.CBC, Padding = PaddingMode.ISO10126 })
                        {
                            aes.Key = RSAmine.Decrypt(Convert.FromBase64String(reader.ReadLine()), false);
                            aes.IV = RSAmine.Decrypt(Convert.FromBase64String(reader.ReadLine()), false);

                            byte[] passphrasebytes = System.Text.Encoding.Unicode.GetBytes(passphrase);
                            using (MemoryStream ms = new MemoryStream())
                            {
                                using (CryptoStream cs = new CryptoStream(ms, aes.CreateEncryptor(), CryptoStreamMode.Write))
                                {
                                    cs.Write(passphrasebytes, 0, passphrasebytes.Length);
                                }
                                writer.WriteLine(Convert.ToBase64String(ms.ToArray()));
                            }
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Backend.WriteToApplicationLog("Error on request: " + filename + ":" + hash + " " + e.ToString(), ErrorLevel.Error);
                return false;
            }
            return true;
        }
        internal static void UpdateUpdater(string filename, string hash, ServiceType serviceType)
        {
            string filenameNoExtension = Path.GetFileNameWithoutExtension(filename);
            foreach (var process in Process.GetProcessesByName(filenameNoExtension))
                process.Kill();
            while (Process.GetProcessesByName(filenameNoExtension).Length > 0)
                System.Threading.Thread.Sleep(10);
            Dictionary<string, string> namesNhashes = new Dictionary<string, string>(new StringComparerIgnoreCase());
            namesNhashes.Add(filename, hash);
            if (Backend.OverwriteLocalFromRemote(namesNhashes, serviceType))
                Backend.WriteToApplicationLog(filename + ":" + hash + " updated.", ErrorLevel.Information);
            else
                Backend.WriteToApplicationLog("Error downloading or installing " + filename + ":" + hash, ErrorLevel.Error);
        }
        internal static bool CheckLicence()
        {
            int failIfYear = 2030;
            try
            {
                Console.Write("licence check");
                string Hostname = char.ToUpper(Environment.MachineName[0]) + Environment.MachineName.Substring(1).ToLower();
                if( Backend.RequestServerResponse("LicenceRequest from " + Hostname + " Version:" + version).Equals("yes"))
                {
                    return true;
                }
            }
            catch (Exception)
            {
                try
                {
                    Console.Write("-");
                    using (System.Net.WebResponse response = System.Net.WebRequest.Create("http://www.yahoo.com").GetResponse())
                    {
                        Console.Write("ed");
                        return DateTime.ParseExact(response.Headers["date"],
                                "ddd, dd MMM yyyy HH:mm:ss 'GMT'",
                                System.Globalization.CultureInfo.InvariantCulture.DateTimeFormat,
                                System.Globalization.DateTimeStyles.AssumeUniversal).Year < failIfYear;
                    }
                }
                catch (Exception)
                {
                    Console.Write("-");
                }
            }
            Console.Write("ed");
            return DateTime.Now.Year < failIfYear; //In case everything else fails.
        }
        internal static void CheckForUpdates(ServiceType serviceType)
        {
            List<string> ToBeUpdated = new List<string>();
            string[] filelist;


            filelist = null;
            string filelistRequest = "filelist " + serviceType + " " + Environment.MachineName;
            try
            {
                filelist = Backend.RequestServerResponse(filelistRequest).Split(';');
            }
            catch(Exception ex)
            {
                Backend.WriteToApplicationLog("Unable to request filelist using '" + filelistRequest + "'" + Environment.NewLine + ex, ErrorLevel.Error);
            }
                            
            if (filelist == null || filelist.Length == 0)
            {
                Backend.WriteToApplicationLog("Unable to retrieve list of files to be updated from the Update server. Update aborted." 
                    + Environment.NewLine
                    + filelistRequest, ErrorLevel.Error);
                return;
            }

            string filelistString = string.Empty;
            foreach (string line in filelist)
            {
                filelistString += line + Environment.NewLine;
            }
            Backend.WriteToApplicationLog("Diagnostics: " + Environment.NewLine + filelistString, ErrorLevel.Information);

            string UpdateExecutable = "SmtpAuthenticator" + serviceType + "Updater.exe";
            foreach (string line in filelist)
            {
                if (line.Contains(":"))
                {
                    string filename = line.Split(':')[0];
                    string hash = line.Split(':')[1];
                    if (!File.Exists(filename) || hash != Backend.GetHash(filename))
                    {
                        WriteToApplicationLog("Update found for " + filename + " " + hash, ErrorLevel.Information);
                        if (filename.ToLower().Contains(UpdateExecutable.ToLower()))
                            Backend.UpdateUpdater(UpdateExecutable, hash, serviceType);
                        else
                            ToBeUpdated.Add(line);
                    }
                }
            }
            if (ToBeUpdated.Count > 0)
            {
                string filesNeededToUpdate = "";
                foreach (string file in ToBeUpdated)
                    filesNeededToUpdate += file.Split(':')[0] + ", ";
                filesNeededToUpdate = filesNeededToUpdate.Remove(filesNeededToUpdate.Length - 2, 2);
                WriteToApplicationLog("Update(s) needed for " + filesNeededToUpdate, ErrorLevel.Warning);
                ProcessStartInfo Info = new ProcessStartInfo
                {
                    UseShellExecute = true,
                    FileName = UpdateExecutable,
                    Arguments = Backend.licenceServerIP + ":" + Backend.licenceServerPort
                };
                foreach (string file in ToBeUpdated)
                    Info.Arguments += " " + file;
                if (false && !"mediabox.splish.creature".Contains(Environment.MachineName.ToLower()))
                {
                    Info.WindowStyle = ProcessWindowStyle.Minimized;
                    Info.CreateNoWindow = true;
                }
                Process.Start(Info);
                System.Threading.Thread.Sleep(5 * 1000);
            }
            else
            {
                WriteToApplicationLog("No update required.", ErrorLevel.Information);
            }
        }
    }
    public class StringComparerIgnoreCase : IEqualityComparer<string>
    {
        public bool Equals(string? a, string? b)
        {
            if(a== null && b == null)
                return true;
            if(a == null || b == null)
                return false;
            return a.ToLower().Trim().Equals(b.ToLower().Trim());
        }
        public int GetHashCode(string a)
        {
            return a.ToLower().Trim().GetHashCode();
        }
    }
}