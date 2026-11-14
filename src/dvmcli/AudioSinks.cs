// SPDX-License-Identifier: AGPL-3.0-only
/**
* dvmcli - headless Linux proof of concept for the DVM Desktop Dispatch Console.
*
* Audio outputs for decoded 8 kHz mono 16-bit PCM:
*  - PipeWireSink streams to a PipeWire output (the default, or --output) through pw-play (or pw-cat).
*  - WavSink records to a .wav file.
*/

using System.Diagnostics;

namespace dvmcli
{
    /// <summary>
    /// Destination for decoded 8 kHz mono 16-bit PCM.
    /// </summary>
    public interface IAudioSink : IDisposable
    {
        /// <summary>Writes one block of samples.</summary>
        void Write(short[] samples);
    }

    /// <summary>
    /// Plays PCM on a PipeWire output by piping it into pw-play.
    /// </summary>
    public sealed class PipeWireSink : IAudioSink
    {
        public const int SampleRate = 8000;

        private readonly Process player;
        private readonly Stream stdin;
        private bool failed;

        /// <summary>Command used for playback (pw-play or pw-cat).</summary>
        public string Command { get; }

        /// <summary>
        /// Starts the player process.
        /// </summary>
        /// <param name="command">Explicit player command, or null to find pw-play / pw-cat on PATH.</param>
        /// <param name="target">PipeWire node name to play to, or null for the default output.</param>
        public PipeWireSink(string command = null, string target = null)
        {
            Command = command ?? FindPlayer() ??
                throw new InvalidOperationException("neither pw-play nor pw-cat was found; install pipewire (pacman -S pipewire) or use --no-play");

            ProcessStartInfo psi = new ProcessStartInfo(Command)
            {
                UseShellExecute = false,
                // all three redirected: the child never touches the terminal (the TUI owns it in raw mode)
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            // pw-cat needs --playback; pw-play is pw-cat in playback mode already
            if (Path.GetFileName(Command) == "pw-cat")
                psi.ArgumentList.Add("--playback");
            // --raw: stdin carries bare PCM samples (without it pw-cat tries to open "-" as a sound file)
            foreach (string arg in new[] { "--raw", "--rate", SampleRate.ToString(), "--channels", "1", "--format", "s16",
                                           "--media-role", "Communication" })
                psi.ArgumentList.Add(arg);
            if (!string.IsNullOrEmpty(target))
            {
                psi.ArgumentList.Add("--target");
                psi.ArgumentList.Add(target);
            }
            psi.ArgumentList.Add("-");

            player = Process.Start(psi) ?? throw new InvalidOperationException($"failed to start {Command}");
            stdin = player.StandardInput.BaseStream;
            player.OutputDataReceived += (_, _) => { };
            player.ErrorDataReceived += (_, _) => { };
            player.BeginOutputReadLine();
            player.BeginErrorReadLine();
        }

        /// <summary>Raised once if the player stops unexpectedly.</summary>
        public event Action<string> Failed;

        /// <summary>
        /// Finds pw-play or pw-cat on PATH.
        /// </summary>
        public static string FindPlayer()
        {
            string path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            foreach (string name in new[] { "pw-play", "pw-cat" })
            {
                foreach (string dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
                {
                    string candidate = Path.Combine(dir, name);
                    if (File.Exists(candidate))
                        return candidate;
                }
            }
            return null;
        }

        /// <inheritdoc/>
        public void Write(short[] samples)
        {
            if (failed)
                return;

            byte[] pcm = new byte[samples.Length * 2];
            Buffer.BlockCopy(samples, 0, pcm, 0, pcm.Length);
            try
            {
                stdin.Write(pcm, 0, pcm.Length);
                stdin.Flush();
            }
            catch (IOException)
            {
                failed = true;
                string msg = $"audio: {Command} stopped (exit code {(player.HasExited ? player.ExitCode : -1)}); playback disabled";
                if (Failed != null) Failed(msg); else Console.Error.WriteLine(msg);
            }
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            try { stdin.Dispose(); } catch { /* ignore */ }
            try
            {
                if (!player.WaitForExit(2000))
                    player.Kill();
            }
            catch { /* ignore */ }
            player.Dispose();
        }
    } // public sealed class PipeWireSink

    /// <summary>
    /// Records PCM to an 8 kHz mono 16-bit WAV file.
    /// </summary>
    public sealed class WavSink : IAudioSink
    {
        private readonly FileStream file;
        private long dataBytes;

        /// <summary>
        /// Creates the WAV file (overwriting any existing file).
        /// </summary>
        public WavSink(string path)
        {
            file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
            WriteHeader(0);
        }

        /// <inheritdoc/>
        public void Write(short[] samples)
        {
            byte[] pcm = new byte[samples.Length * 2];
            Buffer.BlockCopy(samples, 0, pcm, 0, pcm.Length);
            file.Write(pcm, 0, pcm.Length);
            dataBytes += pcm.Length;
        }

        private void WriteHeader(long dataLength)
        {
            const int rate = PipeWireSink.SampleRate, channels = 1, bits = 16;
            using BinaryWriter w = new BinaryWriter(file, System.Text.Encoding.ASCII, leaveOpen: true);
            file.Seek(0, SeekOrigin.Begin);
            w.Write("RIFF"u8.ToArray());
            w.Write((uint)(36 + dataLength));
            w.Write("WAVE"u8.ToArray());
            w.Write("fmt "u8.ToArray());
            w.Write(16u);
            w.Write((ushort)1);                          // PCM
            w.Write((ushort)channels);
            w.Write((uint)rate);
            w.Write((uint)(rate * channels * bits / 8)); // byte rate
            w.Write((ushort)(channels * bits / 8));      // block align
            w.Write((ushort)bits);
            w.Write("data"u8.ToArray());
            w.Write((uint)dataLength);
            file.Seek(0, SeekOrigin.End);
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            WriteHeader(dataBytes);
            file.Dispose();
        }
    } // public sealed class WavSink
}
