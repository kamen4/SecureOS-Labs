using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace App;

static class Program
{
    private const string DefaultPassword = "jndlasf074hr";
    private const string DefaultSdRoot = "/sdcard/";

    public static int Main(string[] args)
    {
        try
        {
            var opt = Options.Parse(args);

            EnsureAdbWorks(opt.DeviceSerial);

            var encFiles = ListEncFiles(opt.DeviceSerial, opt.SdRoot);
            if (encFiles.Count == 0)
            {
                Console.WriteLine("No .enc files found on the phone.");
                return 0;
            }

            Console.WriteLine($"Found .enc files: {encFiles.Count}");
            Console.WriteLine($"Backup dir: {Path.GetFullPath(opt.BackupDir)}");
            Console.WriteLine($"Push back: {opt.PushBack}");
            Console.WriteLine($"Delete .enc: {opt.DeleteEnc}");
            Console.WriteLine($"Cleanup local: {opt.Cleanup}");

            if (opt.DeleteEnc && !opt.Yes)
            {
                Console.Write("Are you sure you want to delete .enc AFTER successful recovery? (y/N): ");
                var answer = Console.ReadLine()?.Trim().ToLowerInvariant();
                if (answer != "y" && answer != "yes")
                {
                    Console.WriteLine("Delete disabled. Running without deletion.");
                    opt = opt with { DeleteEnc = false };
                }
            }

            Directory.CreateDirectory(opt.BackupDir);
            var pulledDir = Path.Combine(opt.BackupDir, "pulled");
            var recoveredDir = Path.Combine(opt.BackupDir, "recovered");
            Directory.CreateDirectory(pulledDir);
            Directory.CreateDirectory(recoveredDir);

            int ok = 0, fail = 0, deleted = 0, pushed = 0;

            using var aes = CreateAes(opt.Password);

            foreach (var remoteEnc in encFiles)
            {
                var rel = MakeRelative(remoteEnc, opt.SdRoot);
                var localEnc = Path.Combine(pulledDir, rel.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(localEnc)!);

                var localOutRel = rel.EndsWith(".enc", StringComparison.OrdinalIgnoreCase)
                    ? rel.Substring(0, rel.Length - 4)
                    : rel + ".dec";
                var localOut = Path.Combine(recoveredDir, localOutRel.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(localOut)!);

                var remoteOut = remoteEnc.EndsWith(".enc", StringComparison.OrdinalIgnoreCase)
                    ? remoteEnc.Substring(0, remoteEnc.Length - 4)
                    : remoteEnc + ".dec";

                Console.WriteLine();
                Console.WriteLine($"[FILE] {remoteEnc}");

                if (!Adb.Pull(opt.DeviceSerial, remoteEnc, localEnc, out var pullErr))
                {
                    Console.WriteLine($"  FAIL pull: {pullErr}");
                    fail++;
                    continue;
                }

                try
                {
                    DecryptFileAesCbcPkcs7(aes, localEnc, localOut);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"  FAIL decrypt: {ex.GetType().Name}: {ex.Message}");
                    fail++;
                    continue;
                }

                bool pushOk = true;
                string? finalRemoteOut = null;

                if (opt.PushBack)
                {
                    var remoteDir = GetPosixDir(remoteOut);
                    if (!Adb.Shell(opt.DeviceSerial, $"mkdir -p -- {ShQuote(remoteDir)}", out var mkErr))
                    {
                        Console.WriteLine($"  FAIL mkdir: {mkErr}");
                        fail++;
                        continue;
                    }

                    finalRemoteOut = remoteOut;

                    if (!Adb.Push(opt.DeviceSerial, localOut, finalRemoteOut, out var pushErr))
                    {
                        Console.WriteLine($"  FAIL push: {pushErr}");
                        pushOk = false;
                    }
                    else
                    {
                        pushed++;
                    }
                }

                if (opt.DeleteEnc && ( !opt.PushBack || pushOk ))
                {
                    if (Adb.Shell(opt.DeviceSerial, $"rm -f -- {ShQuote(remoteEnc)}", out var rmErr))
                        deleted++;
                    else
                        Console.WriteLine($"  WARN rm failed: {rmErr}");
                }

                ok++;
                if (opt.PushBack)
                    Console.WriteLine($"  OK  -> pushed to: {finalRemoteOut}");
                else
                    Console.WriteLine($"  OK  -> recovered on PC: {localOut}");
            }

            Console.WriteLine();
            Console.WriteLine($"Done. ok={ok} fail={fail} pushed={pushed} deleted={deleted}");

            if (opt.Cleanup)
            {
                Console.WriteLine("Cleaning up local files...");
                try
                {
                    if (Directory.Exists(pulledDir))
                        Directory.Delete(pulledDir, true);
                    if (Directory.Exists(recoveredDir))
                        Directory.Delete(recoveredDir, true);
                    
                    // Delete backup dir if empty
                    if (Directory.Exists(opt.BackupDir) && !Directory.EnumerateFileSystemEntries(opt.BackupDir).Any())
                        Directory.Delete(opt.BackupDir);
                    
                    Console.WriteLine("Cleanup complete.");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Cleanup warning: {ex.Message}");
                }
            }

            return fail == 0 ? 0 : 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 2;
        }
    }

    private static Aes CreateAes(string password)
    {
        byte[] key;
        using (var sha = SHA256.Create())
            key = sha.ComputeHash(Encoding.UTF8.GetBytes(password));

        var aes = Aes.Create();
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        aes.KeySize = 256;
        aes.Key = key;
        aes.IV = new byte[16]; // all zeros
        return aes;
    }

    private static void DecryptFileAesCbcPkcs7(Aes aes, string inPath, string outPath)
    {
        using var inStream = File.OpenRead(inPath);
        using var outStream = File.Create(outPath);
        using var decryptor = aes.CreateDecryptor(aes.Key, aes.IV);
        using var crypto = new CryptoStream(inStream, decryptor, CryptoStreamMode.Read);

        crypto.CopyTo(outStream);
        outStream.Flush(true);
    }

    private static void EnsureAdbWorks(string? serial)
    {
        if (!Adb.Run(serial, "version", out _, out var err))
            throw new InvalidOperationException("adb not found. Install Android Platform Tools and add adb to PATH.");

        Adb.Run(null, "devices", out var devices, out _);
        if (!devices.Contains("\tdevice", StringComparison.Ordinal))
            Console.WriteLine("Warning: `adb devices` does not show an authorized device (device). Check the phone prompt/cable.");
    }

    private static List<string> ListEncFiles(string? serial, string sdRoot)
    {
        var cmd = $"find {ShQuote(sdRoot)} -type f -name '*.enc' 2>/dev/null";
        if (!Adb.Shell(serial, cmd, out var output, out var err))
            throw new InvalidOperationException("Failed to list .enc files via adb shell: " + err);

        return output
            .Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(s => s.EndsWith(".enc", StringComparison.OrdinalIgnoreCase))
            .Distinct()
            .ToList();
    }

    private static string MakeRelative(string fullPosixPath, string rootPosix)
    {
        var root = rootPosix.TrimEnd('/');
        if (fullPosixPath.StartsWith(root + "/", StringComparison.Ordinal))
            return fullPosixPath.Substring(root.Length + 1);
        return fullPosixPath.TrimStart('/');
    }

    private static string GetPosixDir(string posixPath)
    {
        var idx = posixPath.LastIndexOf('/');
        return idx <= 0 ? "/" : posixPath.Substring(0, idx);
    }

    private static string ShQuote(string s)
    {
        return "'" + s.Replace("'", "'\\''") + "'";
    }

    private record Options(string BackupDir, bool PushBack, bool DeleteEnc, bool Yes, bool Cleanup, string Password, string SdRoot, string? DeviceSerial)
    {
        public static Options Parse(string[] args)
        {
            if (args.Length == 0)
            {
                PrintHelp();
                Environment.Exit(1);
            }

            string backup = @".\backup";
            bool push = false;
            bool delete = false;
            bool yes = false;
            bool cleanup = false;
            string password = DefaultPassword;
            string sdRoot = DefaultSdRoot;
            string? serial = null;

            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                string Next() => ( i + 1 < args.Length ) ? args[++i] : throw new ArgumentException($"Missing value for {a}");

                switch (a)
                {
                    case "--backup": backup = Next(); break;
                    case "--push": push = true; break;
                    case "--delete": delete = true; break;
                    case "--yes": yes = true; break;
                    case "--cleanup": cleanup = true; break;
                    case "--password": password = Next(); break;
                    case "--sdroot": sdRoot = Next(); break;
                    case "--device": serial = Next(); break;
                    case "--help":
                    case "-h":
                        PrintHelp();
                        Environment.Exit(0);
                        break;
                    default:
                        throw new ArgumentException($"Unknown argument: {a} (use --help)");
                }
            }

            return new Options(backup, push, delete, yes, cleanup, password, sdRoot, serial);
        }

        private static void PrintHelp()
        {
            Console.WriteLine(@$"DecryptorCLI - A utility to decrypt .enc files on an Android device via ADB

Usage:
  dotnet run -- [options]

Options:
  --backup <dir>      Local directory to save pulled and decrypted files
                      (default: .\backup)

  --push              Push decrypted files back to the device
                      If a file already exists, a .recovered suffix is added

  --delete            Delete original .enc files from the device after successful decryption
                      Requires confirmation (or use --yes to skip)

  --yes               Automatically confirm deletion of .enc files (no prompt)

  --cleanup           Delete local 'pulled' and 'recovered' folders after completion
                      Useful when using --push to avoid leaving files on PC

  --password <p>      Password for decryption (SHA256 -> AES-256-CBC key)
                      (default: {DefaultPassword})

  --sdroot <path>     Root directory to search for .enc files on the device
                      (default: {DefaultSdRoot})

  --device <serial>   Device serial number (if multiple devices are connected)
                      Get serial numbers with: adb devices

  --help, -h          Show this help message

Examples:
  dotnet run -- --backup ./output
  dotnet run -- --backup ./output --push --delete --yes
  dotnet run -- --push --cleanup --yes
  dotnet run -- --password mySecret123 --sdroot /storage/emulated/0");
        }
    }
}

static class Adb
{
    public static bool Pull(string? serial, string remote, string local, out string err)
        => Run(serial, new[] { "pull", remote, local }, out _, out err);

    public static bool Push(string? serial, string local, string remote, out string err)
        => Run(serial, new[] { "push", local, remote }, out _, out err);

    public static bool Shell(string? serial, string shellCommand, out string output, out string err)
        => Run(serial, new[] { "shell", shellCommand }, out output, out err);

    public static bool Shell(string? serial, string shellCommand, out string err)
        => Run(serial, new[] { "shell", shellCommand }, out _, out err);

    public static bool Run(string? serial, string subcommand, out string output, out string err)
        => Run(serial, new[] { subcommand }, out output, out err);

    public static bool Run(string? serial, IEnumerable<string> args, out string output, out string err)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "adb",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        if (!string.IsNullOrWhiteSpace(serial))
        {
            psi.ArgumentList.Add("-s");
            psi.ArgumentList.Add(serial);
        }

        foreach (var a in args)
            psi.ArgumentList.Add(a);

        using var p = Process.Start(psi)!;
        output = p.StandardOutput.ReadToEnd();
        err = p.StandardError.ReadToEnd();
        p.WaitForExit();
        return p.ExitCode == 0;
    }
}