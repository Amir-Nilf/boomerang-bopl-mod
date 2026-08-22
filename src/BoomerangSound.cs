using System;
using System.IO;
using System.Reflection;
using UnityEngine;

namespace Boomerang
{
    // Plays a small chime when a card is caught.
    //
    // The game's AudioManager only knows its own fixed set of sound names, so a custom
    // sound has to be loaded and played directly. Unity cannot decode an arbitrary audio
    // file from memory at runtime either, so the clip ships as a plain 16-bit PCM WAV and
    // is parsed by hand below -- that format is simple enough to read in a few lines and
    // avoids depending on anything at runtime.
    //
    // Resources/Pickup.wav can be replaced with any 16-bit PCM WAV, mono or stereo, at any
    // sample rate, with no code change. Rebuild after replacing it, since resources are
    // embedded at compile time.
    public static class BoomerangSound
    {
        private const string ResourceName = "Boomerang.Pickup.wav";

        private const float Volume = 0.6f;

        private static AudioClip clip;
        private static bool loadAttempted;

        public static void PlayPickup(Vector3 position)
        {
            if (!loadAttempted)
            {
                loadAttempted = true;
                clip = Load();
            }
            if (clip == null)
            {
                return;
            }
            // Spawns its own short-lived source and cleans itself up, which suits a
            // fire-and-forget one-shot on an object that may be destroyed moments later.
            AudioSource.PlayClipAtPoint(clip, position, Volume);
        }

        private static AudioClip Load()
        {
            byte[] data;
            using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName))
            {
                if (stream == null)
                {
                    Plugin.Log.LogWarning($"Boomerang: '{ResourceName}' not found, so catching it will be "
                                          + "silent.");
                    return null;
                }
                using (MemoryStream buffer = new MemoryStream())
                {
                    stream.CopyTo(buffer);
                    data = buffer.ToArray();
                }
            }

            try
            {
                return ParseWav(data);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("Boomerang: the pickup chime could not be read, so catching a card "
                                      + $"will be silent: {ex.Message}");
                return null;
            }
        }

        // Minimal RIFF/WAVE reader: walks the chunk list for "fmt " and "data" rather than
        // assuming they sit at fixed offsets, because real files often carry extra chunks.
        private static AudioClip ParseWav(byte[] data)
        {
            if (data.Length < 12
                || data[0] != 'R' || data[1] != 'I' || data[2] != 'F' || data[3] != 'F'
                || data[8] != 'W' || data[9] != 'A' || data[10] != 'V' || data[11] != 'E')
            {
                throw new Exception("not a RIFF/WAVE file");
            }

            int channels = 0;
            int sampleRate = 0;
            int bitsPerSample = 0;
            int dataOffset = -1;
            int dataLength = 0;

            int position = 12;
            while (position + 8 <= data.Length)
            {
                string chunkId = "" + (char)data[position] + (char)data[position + 1]
                                 + (char)data[position + 2] + (char)data[position + 3];
                int chunkSize = BitConverter.ToInt32(data, position + 4);
                int body = position + 8;

                if (chunkId == "fmt ")
                {
                    channels = BitConverter.ToInt16(data, body + 2);
                    sampleRate = BitConverter.ToInt32(data, body + 4);
                    bitsPerSample = BitConverter.ToInt16(data, body + 14);
                }
                else if (chunkId == "data")
                {
                    dataOffset = body;
                    dataLength = Math.Min(chunkSize, data.Length - body);
                }

                // Chunks are word-aligned, so an odd size is followed by a pad byte.
                position = body + chunkSize + (chunkSize % 2);
            }

            if (dataOffset < 0 || channels <= 0 || sampleRate <= 0)
            {
                throw new Exception("missing fmt or data chunk");
            }
            if (bitsPerSample != 16)
            {
                throw new Exception($"only 16-bit PCM is supported, this is {bitsPerSample}-bit");
            }

            int sampleCount = dataLength / 2;
            float[] samples = new float[sampleCount];
            for (int i = 0; i < sampleCount; i++)
            {
                samples[i] = BitConverter.ToInt16(data, dataOffset + i * 2) / 32768f;
            }

            AudioClip created = AudioClip.Create("CardPickup", sampleCount / channels, channels,
                                                 sampleRate, false);
            created.SetData(samples, 0);
            Plugin.Log.LogInfo($"Boomerang: pickup chime loaded ({sampleCount / channels} samples, "
                               + $"{channels}ch, {sampleRate}Hz).");
            return created;
        }
    }
}
