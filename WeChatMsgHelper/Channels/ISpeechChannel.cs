namespace WeChatMsgHelper.Channels
{
    internal interface ISpeechChannel
    {
        string Name { get; }
        bool IsAvailable { get; }
        bool Initialize(out string error);
        bool Speak(string text, bool interrupt);
        // Submit a short lifecycle prompt without waiting for playback to finish.
        bool Submit(string text, bool interrupt);
        bool Stop();
    }
}
