// SPDX-License-Identifier: AGPL-3.0-only
/**
* dvmcli - headless Linux proof of concept for the DVM Desktop Dispatch Console.
*
* Microphone capture from a PipeWire source (the default, or --input) via pw-record, delivered as
* 20 ms blocks of 8 kHz mono 16-bit PCM.
*/

using System.Diagnostics;

namespace dvmcli
{
    /// <summary>
    /// Captures 8 kHz mono audio from PipeWire.
    /// </summary>
    public sealed class PipeWireCapture : IDisposable
    {
        private readonly Process recorder;
        private readonly Thread reader;
        private volatile bool stopping;

        /// <summary>Raised on the capture thread with each 160-sample block.</summary>
        public event Action<short[]> Block;

        /// <summary>Raised if the recorder stops unexpectedly.</summary>
        public event Action<string> Failed;

        /// <summary>Command used for capture.</summary>
        public string Command { get; }

        /// <summary>
        /// Starts pw-record (or pw-cat --record).
        /// </summary>
        /// <param name="command">Explicit recorder command, or null to find pw-record / pw-cat on PATH.</param>
        /// <param name="target">PipeWire node name to record from, or null for the default input.</param>
        public PipeWireCapture(string command = null, string target = null)
        {
            Command = command ?? FindRecorder() ??
                throw new InvalidOperationException("neither pw-record nor pw-cat was found; install pipewire");

            ProcessStartInfo psi = new ProcessStartInfo(Command)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true     // keep the child off the terminal
            };
            if (Path.GetFileName(Command) == "pw-cat")
                psi.ArgumentList.Add("--record");
            foreach (string arg in new[] { "--raw", "--rate", PipeWireSink.SampleRate.ToString(), "--channels", "1", "--format", "s16",
                                           "--media-role", "Communication" })
                psi.ArgumentList.Add(arg);
            if (!string.IsNullOrEmpty(target))
            {
                psi.ArgumentList.Add("--target");
                psi.ArgumentList.Add(target);
            }
            psi.ArgumentList.Add("-");

            recorder = Process.Start(psi) ?? throw new InvalidOperationException($"failed to start {Command}");
            recorder.ErrorDataReceived += (_, _) => { };   // drain stderr so it can't block
            recorder.BeginErrorReadLine();

            reader = new Thread(ReadLoop) { IsBackground = true, Name = "pw-record reader" };
            reader.Start();
        }

        /// <summary>
        /// Finds pw-record or pw-cat on PATH.
        /// </summary>
        public static string FindRecorder()
        {
            string path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            foreach (string name in new[] { "pw-record", "pw-cat" })
                foreach (string dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
                {
                    string candidate = Path.Combine(dir, name);
                    if (File.Exists(candidate))
                        return candidate;
                }
            return null;
        }

        private void ReadLoop()
        {
            Stream stdout = recorder.StandardOutput.BaseStream;
            byte[] buf = new byte[P25VoiceReceiver.FrameSamples * 2];
            try
            {
                while (!stopping)
                {
                    int got = 0;
                    while (got < buf.Length)
                    {
                        int r = stdout.Read(buf, got, buf.Length - got);
                        if (r <= 0)
                        {
                            if (!stopping)
                                Failed?.Invoke($"{Path.GetFileName(Command)} stopped");
                            return;
                        }
                        got += r;
                    }

                    short[] samples = new short[P25VoiceReceiver.FrameSamples];
                    Buffer.BlockCopy(buf, 0, samples, 0, buf.Length);
                    Block?.Invoke(samples);
                }
            }
            catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException)
            {
                if (!stopping)
                    Failed?.Invoke($"{Path.GetFileName(Command)}: {ex.Message}");
            }
        }

        /// <summary>
        /// Stops capture.
        /// </summary>
        public void Dispose()
        {
            stopping = true;
            try
            {
                if (!recorder.HasExited)
                    recorder.Kill();
                recorder.WaitForExit(1000);
            }
            catch { /* ignore */ }
            reader.Join(1000);
            recorder.Dispose();
        }
    } // public sealed class PipeWireCapture
}
