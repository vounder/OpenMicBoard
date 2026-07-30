using NAudio.Vorbis;
using NAudio.Wave;

namespace OpenMicBoard.Audio.Windows;

internal sealed class AudioFileSource : IAudioFileSource
{
    private readonly IDisposable _owner;
    private readonly ISampleProvider _provider;

    private AudioFileSource(IDisposable owner, ISampleProvider provider)
    {
        _owner = owner;
        _provider = provider;
    }

    public WaveFormat WaveFormat => _provider.WaveFormat;

    public static AudioFileSource Open(string filePath)
    {
        var extension = Path.GetExtension(filePath);
        if (extension.Equals(".ogg", StringComparison.OrdinalIgnoreCase))
        {
            var reader = new VorbisWaveReader(filePath);
            return new AudioFileSource(reader, reader.ToSampleProvider());
        }

        if (extension.Equals(".wav", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".mp3", StringComparison.OrdinalIgnoreCase))
        {
            var reader = new AudioFileReader(filePath);
            return new AudioFileSource(reader, reader);
        }

        throw new NotSupportedException("OpenMicBoard supports WAV, MP3, and OGG Vorbis files.");
    }

    public int Read(float[] buffer, int offset, int count) => _provider.Read(buffer, offset, count);

    public void Dispose() => _owner.Dispose();
}
