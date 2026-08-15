namespace MetaVoiceType.Transcription;

public interface IAsrChannel : IDisposable
{
    void Accept(float[] samples);
    void Finish();
    bool IsReady();
    string Decode();
    string CurrentText { get; }
}

public interface IAsrBackend : IDisposable
{
    string Acceleration { get; }
    IAsrChannel CreateStream(string language);
}
