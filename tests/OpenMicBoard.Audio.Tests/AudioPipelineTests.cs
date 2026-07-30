using NAudio.Wave;
using OpenMicBoard.Audio.Windows;

namespace OpenMicBoard.Audio.Tests;

public sealed class AudioPipelineTests
{
    [Fact]
    public void AdaptiveBuffer_PrimesAndReturnsStereoFrames()
    {
        var buffer = new AdaptiveStereoBuffer(capacityFrames: 16_384, targetFrames: 4_800);
        var source = Enumerable.Range(0, 6_000)
            .SelectMany(frame => new[] { frame / 6_000f, -(frame / 6_000f) })
            .ToArray();
        var destination = new float[960 * 2];

        Assert.Equal(6_000, buffer.Write(source));
        Assert.Equal(destination.Length / 2, buffer.Read(destination));

        Assert.Contains(destination, sample => Math.Abs(sample) > 0.001f);
        Assert.InRange(buffer.AvailableFrames, 4_900, 5_100);
        Assert.Equal(0, buffer.UnderrunFrames);
    }

    [Fact]
    public void AdaptiveBuffer_DropsNewFramesInsteadOfOverwritingUnreadAudio()
    {
        var buffer = new AdaptiveStereoBuffer(capacityFrames: 8, targetFrames: 2);
        var source = new float[20];

        Assert.Equal(8, buffer.Write(source));
        Assert.Equal(2, buffer.DroppedFrames);
    }

    [Fact]
    public void MicrophoneConverter_PreservesEveryFrameAcross48KhzCallbacks()
    {
        var buffer = new AdaptiveStereoBuffer(capacityFrames: 64, targetFrames: 2);
        var converter = new MicrophonePcmConverter(
            new WaveFormat(48_000, 16, 1),
            buffer);

        converter.ConvertAndWrite(CreatePcm16Frames(0, 4), byteCount: 8);
        converter.ConvertAndWrite(CreatePcm16Frames(4, 4), byteCount: 8);

        Assert.Equal(8, buffer.AvailableFrames);
    }

    [Fact]
    public void MicrophoneConverter_PreservesFractionalPhaseAcross44Point1KhzCallbacks()
    {
        const int framesPerCallback = 441;
        const int callbackCount = 3;
        var buffer = new AdaptiveStereoBuffer(capacityFrames: 2_048, targetFrames: 2);
        var converter = new MicrophonePcmConverter(
            new WaveFormat(44_100, 16, 1),
            buffer);

        for (var callback = 0; callback < callbackCount; callback++)
        {
            var firstFrame = callback * framesPerCallback;
            var source = CreatePcm16Frames(firstFrame, framesPerCallback);
            converter.ConvertAndWrite(source, source.Length);
        }

        var sourceFrames = framesPerCallback * callbackCount;
        var ratio = 44_100d / 48_000d;
        var expectedFrames = (int)Math.Floor((sourceFrames - 1) / ratio) + 1;
        Assert.Equal(expectedFrames, buffer.AvailableFrames);
    }

    [Fact]
    public void Limiter_KeepsLinkedStereoBelowCeiling()
    {
        var source = new ConstantSampleProvider(2f, 4_096);
        var limiter = new LookaheadLimiterSampleProvider(source, lookaheadMilliseconds: 1);
        var output = new float[4_096];

        limiter.Read(output, 0, output.Length);

        Assert.InRange(output.Max(sample => Math.Abs(sample)), 0f, 0.891251f);
        Assert.Contains(output, sample => Math.Abs(sample) > 0.8f);
    }

    [Fact]
    public void Mixer_RetriggerReplacesTheSameSound()
    {
        using var mixer = new FixedVoiceMixer(null);
        var id = Guid.NewGuid();
        mixer.AddOrReplace(id, new OwnedConstantSource(0.2f), 1f);
        mixer.AddOrReplace(id, new OwnedConstantSource(0.7f), 1f);
        var output = new float[32];

        mixer.Read(output, 0, output.Length);

        Assert.All(output, sample => Assert.Equal(0.7f, sample, precision: 3));
    }

    [Fact]
    public void Mixer_NinthVoiceStealsTheOldestSlot()
    {
        using var mixer = new FixedVoiceMixer(null);
        for (var voice = 1; voice <= 9; voice++)
        {
            mixer.AddOrReplace(Guid.NewGuid(), new OwnedConstantSource(voice / 100f), 1f);
        }

        var output = new float[32];
        mixer.Read(output, 0, output.Length);

        Assert.All(output, sample => Assert.Equal(0.44f, sample, precision: 3));
    }

    private static byte[] CreatePcm16Frames(int firstFrame, int frameCount)
    {
        var bytes = new byte[frameCount * sizeof(short)];
        for (var index = 0; index < frameCount; index++)
        {
            var sample = (short)((firstFrame + index) % short.MaxValue);
            BitConverter.TryWriteBytes(bytes.AsSpan(index * sizeof(short)), sample);
        }

        return bytes;
    }

    private sealed class ConstantSampleProvider(float value, int sampleCount) : ISampleProvider
    {
        private int _remaining = sampleCount;

        public WaveFormat WaveFormat { get; } =
            WaveFormat.CreateIeeeFloatWaveFormat(48_000, 2);

        public int Read(float[] buffer, int offset, int count)
        {
            var read = Math.Min(count, _remaining);
            Array.Fill(buffer, value, offset, read);
            _remaining -= read;
            return read;
        }
    }

    private sealed class OwnedConstantSource(float value) : IAudioFileSource
    {
        public WaveFormat WaveFormat { get; } =
            WaveFormat.CreateIeeeFloatWaveFormat(48_000, 2);

        public int Read(float[] buffer, int offset, int count)
        {
            Array.Fill(buffer, value, offset, count);
            return count;
        }

        public void Dispose()
        {
        }
    }
}
