// SPDX-License-Identifier: AGPL-3.0-only
/**
* dvmcli - headless Linux proof of concept for the DVM Desktop Dispatch Console.
*
* PipeWire audio device lookup: lists outputs (sinks) and inputs (sources) and the current
* defaults by reading `pw-dump`, resolves --output / --input to a device, and describes the
* devices in use for the startup log.
*/

using System.Diagnostics;
using System.Text.Json;

namespace dvmcli
{
    /// <summary>
    /// One PipeWire audio node.
    /// </summary>
    public sealed class AudioDevice
    {
        /// <summary>Object ID (what wpctl status shows; can change after a reboot or replug).</summary>
        public uint Id { get; init; }
        /// <summary>node.name, stable across reboots; passed to pw-play / pw-record as --target.</summary>
        public string Name { get; init; }
        /// <summary>node.description, the human-readable name.</summary>
        public string Description { get; init; }
        /// <summary>object.serial.</summary>
        public string Serial { get; init; }

        /// <summary>Description, falling back to the node name.</summary>
        public string Label => string.IsNullOrWhiteSpace(Description) ? Name : Description;
    }

    /// <summary>
    /// Snapshot of PipeWire's audio outputs and inputs.
    /// </summary>
    public sealed class AudioDeviceList
    {
        public List<AudioDevice> Outputs { get; } = new List<AudioDevice>();
        public List<AudioDevice> Inputs { get; } = new List<AudioDevice>();
        /// <summary>node.name of the current default output, or null.</summary>
        public string DefaultOutputName { get; set; }
        /// <summary>node.name of the current default input, or null.</summary>
        public string DefaultInputName { get; set; }

        public AudioDevice DefaultOutput => Outputs.FirstOrDefault(d => d.Name == DefaultOutputName);
        public AudioDevice DefaultInput => Inputs.FirstOrDefault(d => d.Name == DefaultInputName);

        /// <summary>
        /// Reads the device list from pw-dump. Throws InvalidOperationException if that isn't possible.
        /// </summary>
        public static AudioDeviceList Query()
        {
            string pwDump = FindOnPath("pw-dump") ?? throw new InvalidOperationException("pw-dump not found");
            ProcessStartInfo psi = new ProcessStartInfo(pwDump)
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using Process p = Process.Start(psi) ?? throw new InvalidOperationException("failed to start pw-dump");
            p.StandardInput.Close();
            p.ErrorDataReceived += (_, _) => { };
            p.BeginErrorReadLine();
            Task<string> stdout = p.StandardOutput.ReadToEndAsync();
            if (!p.WaitForExit(5000) || !stdout.Wait(1000))
            {
                try { p.Kill(); } catch { /* ignore */ }
                throw new InvalidOperationException("pw-dump didn't answer (is PipeWire running?)");
            }
            if (p.ExitCode != 0)
                throw new InvalidOperationException($"pw-dump failed (exit code {p.ExitCode}; is PipeWire running?)");
            return Parse(stdout.Result);
        }

        /// <summary>
        /// Parses pw-dump's JSON output.
        /// </summary>
        public static AudioDeviceList Parse(string json)
        {
            AudioDeviceList list = new AudioDeviceList();
            using JsonDocument doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                throw new InvalidOperationException("unexpected pw-dump output");

            foreach (JsonElement obj in doc.RootElement.EnumerateArray())
            {
                string type = Str(obj, "type");
                if (type == "PipeWire:Interface:Node")
                {
                    if (!obj.TryGetProperty("info", out JsonElement info) || !info.TryGetProperty("props", out JsonElement props))
                        continue;
                    string cls = Str(props, "media.class") ?? string.Empty;
                    AudioDevice dev = new AudioDevice
                    {
                        Id = obj.TryGetProperty("id", out JsonElement id) && id.TryGetUInt32(out uint n) ? n : 0,
                        Name = Str(props, "node.name"),
                        Description = Str(props, "node.description") ?? Str(props, "node.nick"),
                        Serial = Str(props, "object.serial")
                    };
                    if (string.IsNullOrEmpty(dev.Name))
                        continue;
                    // Audio/Sink, Audio/Source and their /Virtual variants; not streams or Audio/Device
                    if (cls == "Audio/Sink" || cls.StartsWith("Audio/Sink/"))
                        list.Outputs.Add(dev);
                    else if (cls == "Audio/Source" || cls.StartsWith("Audio/Source/"))
                        list.Inputs.Add(dev);
                }
                else if (type == "PipeWire:Interface:Metadata")
                {
                    if (!obj.TryGetProperty("props", out JsonElement mprops) || Str(mprops, "metadata.name") != "default")
                        continue;
                    if (!obj.TryGetProperty("metadata", out JsonElement entries) || entries.ValueKind != JsonValueKind.Array)
                        continue;
                    foreach (JsonElement e in entries.EnumerateArray())
                    {
                        string key = Str(e, "key");
                        if (key != "default.audio.sink" && key != "default.audio.source")
                            continue;
                        string name = MetadataName(e);
                        if (key == "default.audio.sink") list.DefaultOutputName = name;
                        else list.DefaultInputName = name;
                    }
                }
            }

            list.Outputs.Sort((a, b) => a.Id.CompareTo(b.Id));
            list.Inputs.Sort((a, b) => a.Id.CompareTo(b.Id));
            return list;
        }

        /// <summary>
        /// Finds the device the user named: an ID or serial, an exact node name or description,
        /// or any part of the description or node name that matches only one device (case ignored).
        /// Throws ArgumentException listing the choices when nothing or more than one matches.
        /// </summary>
        public static AudioDevice Resolve(List<AudioDevice> devices, string spec, string option)
        {
            spec = spec?.Trim() ?? string.Empty;
            if (spec.Length == 0)
                throw new ArgumentException($"{option} needs a device name");

            AudioDevice hit = null;
            if (uint.TryParse(spec, out uint num))
                hit = devices.FirstOrDefault(d => d.Id == num) ?? devices.FirstOrDefault(d => d.Serial == spec);
            hit ??= devices.FirstOrDefault(d => d.Name == spec)
                 ?? devices.FirstOrDefault(d => string.Equals(d.Description, spec, StringComparison.OrdinalIgnoreCase));
            if (hit != null)
                return hit;

            List<AudioDevice> partial = devices.Where(d =>
                (d.Description ?? string.Empty).Contains(spec, StringComparison.OrdinalIgnoreCase) ||
                d.Name.Contains(spec, StringComparison.OrdinalIgnoreCase)).ToList();
            if (partial.Count == 1)
                return partial[0];

            string kind = option == "--input" ? "inputs" : "outputs";
            if (partial.Count > 1)
                throw new ArgumentException($"{option} '{spec}' matches more than one device; be more specific:" + Environment.NewLine +
                                            string.Join(Environment.NewLine, partial.Select(d => $"  {d.Id,4}  {d.Label}")));
            throw new ArgumentException($"no audio device matches {option} '{spec}'. The {kind} are:" + Environment.NewLine +
                                        (devices.Count == 0 ? "  (none)" : string.Join(Environment.NewLine, devices.Select(d => $"  {d.Id,4}  {d.Label}"))));
        }

        private static string MetadataName(JsonElement entry)
        {
            if (!entry.TryGetProperty("value", out JsonElement v))
                return null;
            if (v.ValueKind == JsonValueKind.Object)
                return Str(v, "name");
            if (v.ValueKind == JsonValueKind.String)
            {
                string s = v.GetString();
                try
                {
                    using JsonDocument inner = JsonDocument.Parse(s);
                    if (inner.RootElement.ValueKind == JsonValueKind.Object)
                        return Str(inner.RootElement, "name");
                }
                catch (JsonException) { /* plain string */ }
                return s;
            }
            return null;
        }

        private static string Str(JsonElement obj, string name)
        {
            if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(name, out JsonElement v))
                return null;
            return v.ValueKind switch
            {
                JsonValueKind.String => v.GetString(),
                JsonValueKind.Number => v.GetRawText(),
                _ => null
            };
        }

        internal static string FindOnPath(string name)
        {
            string path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            foreach (string dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                string candidate = Path.Combine(dir, name);
                if (File.Exists(candidate))
                    return candidate;
            }
            return null;
        }
    } // public sealed class AudioDeviceList

    /// <summary>
    /// The output and input dvmcli will use, chosen from --output / --input or PipeWire's defaults.
    /// </summary>
    public sealed class AudioSelection
    {
        /// <summary>node.name to pass as --target to pw-play, or null for PipeWire's default.</summary>
        public string OutputTarget { get; private set; }
        /// <summary>node.name to pass as --target to pw-record, or null for PipeWire's default.</summary>
        public string InputTarget { get; private set; }
        /// <summary>Log line describing the output.</summary>
        public string OutputReport { get; private set; }
        /// <summary>Log line describing the input.</summary>
        public string InputReport { get; private set; }

        /// <summary>
        /// Resolves the options against the live device list. A device that was asked for but
        /// can't be found is an error (ArgumentException); not being able to name the defaults is not.
        /// </summary>
        public static AudioSelection Choose(string outputSpec, string inputSpec) =>
            Choose(outputSpec, inputSpec, () => AudioDeviceList.Query());

        /// <summary>Same as <see cref="Choose(string, string)"/> with an injectable device source (self-test).</summary>
        public static AudioSelection Choose(string outputSpec, string inputSpec, Func<AudioDeviceList> query)
        {
            AudioDeviceList devices = null;
            string queryError = null;
            try { devices = query(); }
            catch (Exception ex) when (ex is InvalidOperationException || ex is JsonException || ex is System.ComponentModel.Win32Exception)
            {
                queryError = ex.Message;
            }

            AudioSelection sel = new AudioSelection();
            (sel.OutputTarget, sel.OutputReport) = Pick("output", "--output", outputSpec, devices, queryError,
                                                        devices?.Outputs, devices?.DefaultOutput, devices?.DefaultOutputName);
            (sel.InputTarget, sel.InputReport) = Pick("input", "--input", inputSpec, devices, queryError,
                                                      devices?.Inputs, devices?.DefaultInput, devices?.DefaultInputName);
            return sel;
        }

        private static (string Target, string Report) Pick(string what, string option, string spec, AudioDeviceList devices, string queryError,
                                                            List<AudioDevice> list, AudioDevice def, string defName)
        {
            if (!string.IsNullOrWhiteSpace(spec))
            {
                if (devices == null)
                {
                    // can't check it; hand it to PipeWire as given and say so
                    return (spec.Trim(), $"audio {what}: '{spec.Trim()}' ({option}; couldn't check it: {queryError})");
                }
                AudioDevice dev = AudioDeviceList.Resolve(list, spec, option);
                return (dev.Name, $"audio {what}: {dev.Label} ({option})");
            }

            if (devices == null)
                return (null, $"audio {what}: PipeWire default (couldn't name it: {queryError})");
            if (def != null)
                return (null, $"audio {what}: {def.Label} (system default)");
            if (!string.IsNullOrEmpty(defName))
                return (null, $"audio {what}: {defName} (system default)");
            return (null, $"audio {what}: PipeWire default (none set{(list.Count == 0 ? $"; no {what}s found" : string.Empty)})");
        }
    } // public sealed class AudioSelection
}
