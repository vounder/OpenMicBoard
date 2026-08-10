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
    public void MicrophoneVoiceProcessing_RaisesQuietSpeechWithoutClipping()
    {
        var buffer = new AdaptiveStereoBuffer(capacityFrames: 16_384, targetFrames: 2);
        var provider = new BufferedMicrophoneSampleProvider(
            buffer,
            gain: 1f,
            voiceProcessingEnabled: true,
            noiseGateEnabled: false);
        var source = CreateStereoSine(frameCount: 9_600, amplitude: 0.03f);
        var output = new float[source.Length];
        buffer.Write(source);

        provider.Read(output, 0, output.Length);

        var inputRms = RootMeanSquare(source.AsSpan(source.Length / 2));
        var outputRms = RootMeanSquare(output.AsSpan(output.Length / 2));
        Assert.True(outputRms > inputRms * 1.35f);
        Assert.InRange(output.Max(sample => Math.Abs(sample)), 0f, 0.2f);
    }

    [Fact]
    public void MicrophoneNoiseGate_ClosesLowNoiseAndOpensForSpeech()
    {
        var buffer = new AdaptiveStereoBuffer(capacityFrames: 16_384, targetFrames: 2);
        var provider = new BufferedMicrophoneSampleProvider(
            buffer,
            gain: 1f,
            voiceProcessingEnabled: true,
            noiseGateEnabled: true,
            noiseGateThresholdDb: -40f);
        var noise = CreateStereoSine(frameCount: 4_800, amplitude: 0.001f, frequency: 3_000f);
        var noiseOutput = new float[noise.Length];
        buffer.Write(noise);
        provider.Read(noiseOutput, 0, noiseOutput.Length);

        buffer.Clear();
        var speech = CreateStereoSine(frameCount: 9_600, amplitude: 0.05f);
        var speechOutput = new float[speech.Length];
        buffer.Write(speech);
        provider.Read(speechOutput, 0, speechOutput.Length);

        Assert.True(RootMeanSquare(noiseOutput) < 0.000_1f);
        Assert.True(RootMeanSquare(speechOutput.AsSpan(speechOutput.Length / 2)) > 0.035f);
    }

    [Fact]
    public void MicrophoneMute_ChangesLiveAndClearsOnlyMicrophoneSamples()
    {
        var buffer = new AdaptiveStereoBuffer(capacityFrames: 16_384, targetFrames: 2);
        var provider = new BufferedMicrophoneSampleProvider(
            buffer,
            gain: 1f,
            voiceProcessingEnabled: false,
            noiseGateEnabled: false,
            muted: true);
        var source = CreateStereoSine(frameCount: 4_800, amplitude: 0.1f);
        var muted = new float[source.Length];
        buffer.Write(source);
        provider.Read(muted, 0, muted.Length);

        provider.Configure(
            gain: 1f,
            voiceProcessingEnabled: false,
            noiseGateEnabled: false,
            noiseGateThresholdDb: -50f,
            muted: false);
        buffer.Clear();
        buffer.Write(source);
        var unmuted = new float[source.Length];
        provider.Read(unmuted, 0, unmuted.Length);

        Assert.All(muted, sample => Assert.Equal(0f, sample));
        Assert.True(RootMeanSquare(unmuted) > 0.05f);
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
    public void Limiter_ReleasesQuicklyAfterALoudClip()
    {
        var samples = Enumerable.Repeat(2f, 2_000)
            .Concat(Enumerable.Repeat(0.1f, 10_000))
            .ToArray();
        var limiter = new LookaheadLimiterSampleProvider(
            new SequenceSampleProvider(samples),
            lookaheadMilliseconds: 1);
        var output = new float[samples.Length];

        limiter.Read(output, 0, output.Length);

        Assert.InRange(output[7_000], 0.075f, 0.101f);
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
    public void Mixer_SoundGainUpdatesAnAlreadyPlayingVoice()
    {
        using var mixer = new FixedVoiceMixer(null);
        mixer.AddOrReplace(Guid.NewGuid(), new OwnedConstantSource(0.2f), 1f);
        var before = new float[32];
        var after = new float[32];

        mixer.Read(before, 0, before.Length);
        mixer.SetSoundGain(2f);
        mixer.Read(after, 0, after.Length);

        Assert.All(before, sample => Assert.Equal(0.2f, sample, precision: 3));
        Assert.All(after, sample => Assert.Equal(0.4f, sample, precision: 3));
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

    [Fact]
    public void Mixer_ReportsStartAndNaturalEnd()
    {
        using var mixer = new FixedVoiceMixer(null);
        var changes = CapturePlaybackChanges(mixer);
        var id = Guid.NewGuid();

        mixer.AddOrReplace(id, new OwnedFiniteSource(0.2f, 8), 1f);
        mixer.Read(new float[32], 0, 32);
        mixer.Read(new float[32], 0, 32);

        Assert.Equal(
            [new AudioPlaybackStateChanged(id, true), new AudioPlaybackStateChanged(id, false)],
            changes);
    }

    [Fact]
    public void Mixer_ReportsStopAfterFadeAndStopAll()
    {
        using var mixer = new FixedVoiceMixer(null);
        var changes = CapturePlaybackChanges(mixer);
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        mixer.AddOrReplace(first, new OwnedConstantSource(0.2f), 1f);
        mixer.AddOrReplace(second, new OwnedConstantSource(0.3f), 1f);

        mixer.Stop(first);
        Assert.Equal(2, changes.Count);
        mixer.Read(new float[960], 0, 960);
        mixer.StopAll();
        mixer.Read(new float[960], 0, 960);

        Assert.Equal(
            [
                new AudioPlaybackStateChanged(first, true),
                new AudioPlaybackStateChanged(second, true),
                new AudioPlaybackStateChanged(first, false),
                new AudioPlaybackStateChanged(second, false)
            ],
            changes);
    }

    [Fact]
    public void Mixer_RetriggerDoesNotReportAnIdleState()
    {
        using var mixer = new FixedVoiceMixer(null);
        var changes = CapturePlaybackChanges(mixer);
        var id = Guid.NewGuid();

        mixer.AddOrReplace(id, new OwnedConstantSource(0.2f), 1f);
        mixer.AddOrReplace(id, new OwnedConstantSource(0.7f), 1f);

        Assert.Equal([new AudioPlaybackStateChanged(id, true)], changes);
    }

    [Fact]
    public async Task Mixer_ConcurrentNaturalEndAndRetriggerKeepsReplacementPlaying()
    {
        using var mixer = new FixedVoiceMixer(null);
        var changes = CapturePlaybackChanges(mixer);
        var id = Guid.NewGuid();
        var endingSource = new BlockingEndSource();
        mixer.AddOrReplace(id, endingSource, 1f);

        var endingRead = Task.Run(() => mixer.Read(new float[32], 0, 32));
        Assert.True(endingSource.WaitUntilRead());
        mixer.AddOrReplace(id, new OwnedConstantSource(0.7f), 1f);
        endingSource.Release();
        await endingRead;

        var output = new float[32];
        mixer.Read(output, 0, output.Length);

        Assert.Equal([new AudioPlaybackStateChanged(id, true)], changes);
        Assert.All(output, sample => Assert.Equal(0.7f, sample, precision: 3));
    }

    [Fact]
    public async Task Mixer_ParallelStartsOccupyDistinctVoices()
    {
        using var mixer = new FixedVoiceMixer(null);
        var changes = CapturePlaybackChanges(mixer);
        var ids = Enumerable.Range(0, FixedVoiceMixer.VoiceCapacity)
            .Select(_ => Guid.NewGuid())
            .ToArray();
        using var ready = new CountdownEvent(ids.Length);
        using var release = new ManualResetEventSlim();
        var starts = ids.Select(id => Task.Run(() =>
        {
            ready.Signal();
            release.Wait(TimeSpan.FromSeconds(5));
            mixer.AddOrReplace(id, new OwnedConstantSource(0.05f), 1f);
        })).ToArray();

        Assert.True(ready.Wait(TimeSpan.FromSeconds(5)));
        release.Set();
        await Task.WhenAll(starts);
        var output = new float[32];
        mixer.Read(output, 0, output.Length);

        Assert.Equal(ids.Length, changes.Count);
        Assert.All(ids, id => Assert.Contains(new AudioPlaybackStateChanged(id, true), changes));
        Assert.All(output, sample => Assert.Equal(0.4f, sample, precision: 3));
    }

    [Fact]
    public void Mixer_ReportsVoiceStealAndDispose()
    {
        using var mixer = new FixedVoiceMixer(null);
        var changes = CapturePlaybackChanges(mixer);
        var ids = Enumerable.Range(0, FixedVoiceMixer.VoiceCapacity + 1)
            .Select(_ => Guid.NewGuid())
            .ToArray();

        foreach (var id in ids)
        {
            mixer.AddOrReplace(id, new OwnedConstantSource(0.2f), 1f);
        }

        Assert.Equal(new AudioPlaybackStateChanged(ids[0], false), changes[^2]);
        Assert.Equal(new AudioPlaybackStateChanged(ids[^1], true), changes[^1]);

        mixer.Dispose();

        Assert.Equal(ids.Length * 2, changes.Count);
        Assert.All(ids.Skip(1), id => Assert.Contains(new AudioPlaybackStateChanged(id, false), changes));
    }

    private static List<AudioPlaybackStateChanged> CapturePlaybackChanges(FixedVoiceMixer mixer)
    {
        var changes = new List<AudioPlaybackStateChanged>();
        mixer.PlaybackStateChanged += changes.Add;
        return changes;
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

    private static float[] CreateStereoSine(
        int frameCount,
        float amplitude,
        float frequency = 440f)
    {
        var samples = new float[frameCount * 2];
        for (var frame = 0; frame < frameCount; frame++)
        {
            var sample = amplitude *
                         MathF.Sin(2f * MathF.PI * frequency * frame / 48_000f);
            samples[frame * 2] = sample;
            samples[(frame * 2) + 1] = sample;
        }

        return samples;
    }

    private static float RootMeanSquare(ReadOnlySpan<float> samples)
    {
        var sum = 0d;
        foreach (var sample in samples)
        {
            sum += sample * sample;
        }

        return (float)Math.Sqrt(sum / samples.Length);
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

    private sealed class SequenceSampleProvider(float[] samples) : ISampleProvider
    {
        private int _position;

        public WaveFormat WaveFormat { get; } =
            WaveFormat.CreateIeeeFloatWaveFormat(48_000, 2);

        public int Read(float[] buffer, int offset, int count)
        {
            var read = Math.Min(count, samples.Length - _position);
            samples.AsSpan(_position, read).CopyTo(buffer.AsSpan(offset, read));
            _position += read;
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

    private sealed class OwnedFiniteSource(float value, int sampleCount) : IAudioFileSource
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

        public void Dispose()
        {
        }
    }

    private sealed class BlockingEndSource : IAudioFileSource
    {
        private readonly ManualResetEventSlim _entered = new();
        private readonly ManualResetEventSlim _release = new();

        public WaveFormat WaveFormat { get; } =
            WaveFormat.CreateIeeeFloatWaveFormat(48_000, 2);

        public int Read(float[] buffer, int offset, int count)
        {
            _entered.Set();
            _release.Wait(TimeSpan.FromSeconds(5));
            return 0;
        }

        public bool WaitUntilRead() => _entered.Wait(TimeSpan.FromSeconds(5));

        public void Release() => _release.Set();

        public void Dispose()
        {
            _entered.Dispose();
            _release.Dispose();
        }
    }
}
