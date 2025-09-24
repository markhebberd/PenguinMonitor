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
        internal static string RequestServerResponse(string question)
        {
            try
            {
                using (TcpClient client = new TcpClient(licenceServerIP, licenceServerPort))
                using (NetworkStream stream = client.GetStream())
                {
                    client.ReceiveTimeout = client.SendTimeout = 5000;         
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
                                    if (ms == null) return "failed to encrypt passphrase";
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
                return "fail";
            }
        }
    }
}