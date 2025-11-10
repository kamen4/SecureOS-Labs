using System;
using System.IO;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace WpfApp
{
    public class AppGenerator
    {
        public static async Task GenerateAsync(string textFolder, string textFileName, string textSecret,
            bool signExecutable = false, string certificatePassword = null)
        {
            Directory.CreateDirectory(textFolder);

            string tempDir = Path.Combine(Path.GetTempPath(), "MiniGuiApp_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);

            string tempCertificatePath = null;

            try
            {
                await Run("dotnet", "new console -n App -o .", tempDir);

                string csproj = Path.Combine(tempDir, "App.csproj");
                string text = await File.ReadAllTextAsync(csproj);

                text = Regex.Replace(
                    text,
                    @"<TargetFramework>.*?</TargetFramework>",
                    "<TargetFramework>net8.0-windows</TargetFramework>"
                );

                text = Regex.Replace(
                    text,
                    @"<PropertyGroup>.*?</PropertyGroup>",
                    m => {
                        var group = m.Value;
                        if (group.Contains("<TargetFramework>"))
                        {
                            return group.Replace("</PropertyGroup>",
                                "  <OutputType>WinExe</OutputType>\n  <UseWindowsForms>true</UseWindowsForms>\n</PropertyGroup>");
                        }
                        return group;
                    },
                    RegexOptions.Singleline
                );

                await File.WriteAllTextAsync(csproj, text);

                string programPath = Path.Combine(tempDir, "Program.cs");
                string code = $@"
using System;
using System.Windows.Forms;

class Program
{{
    [STAThread]
    static void Main()
    {{
        Application.EnableVisualStyles();
        Application.Run(new Form
        {{
            Text = ""{Escape(textFileName)}"",
            Width = 400,
            Height = 300,
            Controls =
            {{
                new TextBox
                {{
                    Multiline = true,
                    ReadOnly = true,
                    Dock = DockStyle.Fill,
                    ScrollBars = ScrollBars.Vertical,
                    Text = ""{Escape(textSecret)}""
                }}
            }}
        }});
    }}
}}";
                await File.WriteAllTextAsync(programPath, code);

                string publishDir = Path.Combine(tempDir, "publish");
                await Run("dotnet", $"publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o \"{publishDir}\"", tempDir);

                string builtExe = Path.Combine(publishDir, "App.exe");
                string targetExe = Path.Combine(textFolder, textFileName + ".exe");
                File.Copy(builtExe, targetExe, true);

                if (signExecutable)
                {
                    tempCertificatePath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".pfx");
                    string subjectName = $"CN={textFileName}";
                    GenerateSelfSignedCertificate(tempCertificatePath, subjectName, certificatePassword ?? "password");

                    if (!File.Exists(tempCertificatePath))
                    {
                        throw new FileNotFoundException($"Cannot create sert: {tempCertificatePath}");
                    }

                    await SignExecutableAsync(targetExe, tempCertificatePath, certificatePassword ?? "password");
                }
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
                if (tempCertificatePath != null && File.Exists(tempCertificatePath))
                {
                    try { File.Delete(tempCertificatePath); } catch { }
                }
            }
        }

        private static void GenerateSelfSignedCertificate(string pfxPath, string subjectName, string password)
        {
            using (var rsa = RSA.Create(2048))
            {
                var request = new CertificateRequest(
                    subjectName,
                    rsa,
                    HashAlgorithmName.SHA256,
                    RSASignaturePadding.Pkcs1);

                request.CertificateExtensions.Add(
                    new X509BasicConstraintsExtension(false, false, 0, false));

                request.CertificateExtensions.Add(
                    new X509KeyUsageExtension(
                        X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
                        false));

                request.CertificateExtensions.Add(
                    new X509EnhancedKeyUsageExtension(
                        new OidCollection
                        {
                            new Oid("1.3.6.1.5.5.7.3.3") // Code Signing
                        },
                        false));

                var certificate = request.CreateSelfSigned(
                    DateTimeOffset.UtcNow.AddDays(-1),
                    DateTimeOffset.UtcNow.AddYears(1));

                var pfxData = certificate.Export(X509ContentType.Pkcs12, password);
                File.WriteAllBytes(pfxPath, pfxData);
            }
        }

        private static async Task SignExecutableAsync(string exePath, string certificatePath, string certificatePassword)
        {
            string signTool = FindSignTool();
            if (string.IsNullOrEmpty(signTool))
            {
                throw new InvalidOperationException("SignTool not found. Ensure of Windows SDK is installed");
            }

            if (!File.Exists(exePath))
                throw new FileNotFoundException($"EXE file not found: {exePath}");

            if (!File.Exists(certificatePath))
                throw new FileNotFoundException($"Sert not found: {certificatePath}");

            string arguments = $"sign /f \"{certificatePath}\"";

            if (!string.IsNullOrEmpty(certificatePassword))
            {
                arguments += $" /p \"{certificatePassword}\"";
            }

            arguments += $" /t http://timestamp.digicert.com /v \"{exePath}\"";

            await Run(signTool, arguments, Path.GetDirectoryName(exePath));
        }

        private static string FindSignTool()
        {
            string[] possiblePaths = {
                @"C:\Program Files (x86)\Windows Kits\10\bin\10.0.19041.0\x64\signtool.exe",
                @"C:\Program Files (x86)\Windows Kits\10\bin\10.0.18362.0\x64\signtool.exe",
                @"C:\Program Files (x86)\Windows Kits\10\bin\x64\signtool.exe",
                @"C:\Program Files (x86)\Microsoft SDKs\Windows\v10.0A\bin\NETFX 4.8 Tools\x64\signtool.exe",
                @"C:\Program Files (x86)\Microsoft SDKs\ClickOnce\SignTool\signtool.exe"
            };

            foreach (string path in possiblePaths)
            {
                if (File.Exists(path))
                {
                    return path;
                }
            }

            string signToolFromPath = FindInPath("signtool.exe");
            if (!string.IsNullOrEmpty(signToolFromPath))
            {
                return signToolFromPath;
            }

            return null;
        }

        private static string FindInPath(string fileName)
        {
            string path = Environment.GetEnvironmentVariable("PATH");
            if (path != null)
            {
                foreach (string dir in path.Split(Path.PathSeparator))
                {
                    string fullPath = Path.Combine(dir, fileName);
                    if (File.Exists(fullPath))
                    {
                        return fullPath;
                    }
                }
            }
            return null;
        }

        private static async Task Run(string file, string args, string cwd)
        {
            var psi = new ProcessStartInfo
            {
                FileName = file,
                Arguments = args,
                WorkingDirectory = cwd,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var p = Process.Start(psi)!;
            string o = await p.StandardOutput.ReadToEndAsync();
            string e = await p.StandardError.ReadToEndAsync();
            await p.WaitForExitAsync();

            if (p.ExitCode != 0)
                throw new Exception($"Error of command:\n{file} {args}\n\n{e}\n{o}");
        }

        private static string Escape(string s) =>
            s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n");
    }
}