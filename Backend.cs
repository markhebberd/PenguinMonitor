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
        public static string licenceServerIP = "210.54.37.120"; 
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
                    client.ReceiveTimeout = client.SendTimeout = 2000;         
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