using System.Text;

namespace AudioExport;

/// <summary>One entry in an XACT wave bank.</summary>
public record WaveInfo(int Index, string Codec, int Channels, int SampleRate, int BlockAlign, int BitsPerSample,
	int SamplesPerBlock, long SampleCount, int LoopStart, int LoopLength, string File);

/// <summary>
/// Splits an XACT wave bank (.xwb, version 46) into standalone .wav files.
/// MS-ADPCM entries keep their compression (WAVE_FORMAT_ADPCM header); PCM entries become plain PCM WAVs.
/// </summary>
public static class WaveBankSplitter
{
	private const int CodecPcm = 0, CodecXma = 1, CodecAdpcm = 2, CodecWma = 3;

	// Standard MS-ADPCM predictor coefficients (the XACT variant always uses these seven).
	private static readonly short[] AdpcmCoefs = { 256, 0, 512, -256, 0, 0, 192, 64, 240, 0, 460, -208, 392, -232 };

	public static (string Name, List<WaveInfo> Waves) Split(string xwbPath, string outDir)
	{
		using var br = new BinaryReader(File.OpenRead(xwbPath));
		if (Encoding.ASCII.GetString(br.ReadBytes(4)) != "WBND")
		{
			throw new InvalidDataException("Not an XACT wave bank: " + xwbPath);
		}
		int version = br.ReadInt32();
		br.ReadInt32(); // header version
		var segOffset = new int[5];
		var segLength = new int[5];
		for (int i = 0; i < 5; i++)
		{
			segOffset[i] = br.ReadInt32();
			segLength[i] = br.ReadInt32();
		}

		// Segment 0: bank data.
		br.BaseStream.Position = segOffset[0];
		int flags = br.ReadInt32();
		int count = br.ReadInt32();
		string bankName = Encoding.ASCII.GetString(br.ReadBytes(64)).TrimEnd('\0');
		int metaSize = br.ReadInt32();
		br.ReadInt32(); // name element size
		br.ReadInt32(); // alignment
		if ((flags & 0x20000) != 0)
		{
			throw new NotSupportedException("Compact wave banks aren't supported (not used by Stardew).");
		}
		if (metaSize < 24)
		{
			throw new NotSupportedException($"Unexpected entry metadata size {metaSize}.");
		}

		string bankDir = Path.Combine(outDir, bankName);
		Directory.CreateDirectory(bankDir);
		var waves = new List<WaveInfo>(count);

		for (int i = 0; i < count; i++)
		{
			// Segment 1: entry metadata.
			br.BaseStream.Position = segOffset[1] + (long)i * metaSize;
			br.ReadInt32(); // flags + duration
			int format = br.ReadInt32();
			int offset = br.ReadInt32();
			int length = br.ReadInt32();
			int loopStart = br.ReadInt32();
			int loopLength = br.ReadInt32();

			int codec = format & 3;
			int channels = (format >> 2) & 7;
			int rate = (format >> 5) & 0x3FFFF;
			int align = (format >> 23) & 0xFF;
			int bits = ((format >> 31) & 1) == 1 ? 16 : 8;

			// Segment 4: wave data.
			br.BaseStream.Position = segOffset[4] + (long)offset;
			byte[] data = br.ReadBytes(length);

			string file = $"{i}.wav";
			string path = Path.Combine(bankDir, file);
			WaveInfo info;
			switch (codec)
			{
			case CodecAdpcm:
			{
				int blockAlign = (align + 22) * channels;
				int samplesPerBlock = ((blockAlign - 7 * channels) * 8 / (4 * channels)) + 2;
				long samples = (long)(data.Length / blockAlign) * samplesPerBlock;
				WriteAdpcmWav(path, data, channels, rate, blockAlign, samplesPerBlock);
				info = new WaveInfo(i, "adpcm", channels, rate, blockAlign, 4, samplesPerBlock, samples, loopStart, loopLength, $"{bankName}/{file}");
				break;
			}
			case CodecPcm:
			{
				int blockAlign = channels * bits / 8;
				long samples = data.Length / blockAlign;
				WritePcmWav(path, data, channels, rate, bits);
				info = new WaveInfo(i, "pcm", channels, rate, blockAlign, bits, 1, samples, loopStart, loopLength, $"{bankName}/{file}");
				break;
			}
			default:
				throw new NotSupportedException($"{bankName} #{i}: codec {codec} ({(codec == CodecXma ? "XMA" : "xWMA")}) isn't supported.");
			}
			waves.Add(info);
		}
		return (bankName, waves);
	}

	private static void WriteAdpcmWav(string path, byte[] data, int channels, int rate, int blockAlign, int samplesPerBlock)
	{
		using var bw = new BinaryWriter(File.Create(path));
		// WAVEFORMATEX (18 bytes incl. cbSize) + samplesPerBlock + coefficient count + coefficient pairs.
		int fmtSize = 18 + 2 + 2 + AdpcmCoefs.Length * 2;
		int factSize = 4;
		bw.Write(Encoding.ASCII.GetBytes("RIFF"));
		bw.Write(4 + (8 + fmtSize) + (8 + factSize) + (8 + data.Length));
		bw.Write(Encoding.ASCII.GetBytes("WAVE"));

		bw.Write(Encoding.ASCII.GetBytes("fmt "));
		bw.Write(fmtSize);
		bw.Write((short)2); // WAVE_FORMAT_ADPCM
		bw.Write((short)channels);
		bw.Write(rate);
		bw.Write(rate * blockAlign / samplesPerBlock); // avg bytes/sec
		bw.Write((short)blockAlign);
		bw.Write((short)4); // bits per sample
		bw.Write((short)(4 + AdpcmCoefs.Length * 2)); // cbSize
		bw.Write((short)samplesPerBlock);
		bw.Write((short)(AdpcmCoefs.Length / 2));
		foreach (short c in AdpcmCoefs)
		{
			bw.Write(c);
		}

		bw.Write(Encoding.ASCII.GetBytes("fact"));
		bw.Write(factSize);
		bw.Write((data.Length / blockAlign) * samplesPerBlock);

		bw.Write(Encoding.ASCII.GetBytes("data"));
		bw.Write(data.Length);
		bw.Write(data);
	}

	private static void WritePcmWav(string path, byte[] data, int channels, int rate, int bits)
	{
		using var bw = new BinaryWriter(File.Create(path));
		int blockAlign = channels * bits / 8;
		bw.Write(Encoding.ASCII.GetBytes("RIFF"));
		bw.Write(36 + data.Length);
		bw.Write(Encoding.ASCII.GetBytes("WAVE"));
		bw.Write(Encoding.ASCII.GetBytes("fmt "));
		bw.Write(16);
		bw.Write((short)1); // PCM
		bw.Write((short)channels);
		bw.Write(rate);
		bw.Write(rate * blockAlign);
		bw.Write((short)blockAlign);
		bw.Write((short)bits);
		bw.Write(Encoding.ASCII.GetBytes("data"));
		bw.Write(data.Length);
		bw.Write(data);
	}
}
