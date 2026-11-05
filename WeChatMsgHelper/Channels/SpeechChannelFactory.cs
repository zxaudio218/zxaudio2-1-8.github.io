namespace WeChatMsgHelper.Channels
{
    internal static class SpeechChannelFactory
    {
        internal static ISpeechChannel Create(AppConfig config)
        {
            config.SpeechChannel = SpeechChannelNames.Normalize(config.SpeechChannel);
            if (config.SpeechChannel == SpeechChannelNames.Auto) return new AutoSpeechChannel(config);
            if (config.SpeechChannel == SpeechChannelNames.BaoyiIndependent)
                return new BaoyiChannel(config, BaoyiChannelMode.Independent);
            if (config.SpeechChannel == SpeechChannelNames.BaoyiScreenReader)
                return new BaoyiChannel(config, BaoyiChannelMode.ScreenReader);
            if (config.SpeechChannel == SpeechChannelNames.Sapi5) return new Sapi5Channel(config);
            if (config.SpeechChannel == SpeechChannelNames.ZhengduScreenReader)
                return new ZhengduChannel(config, ZhengduChannelMode.ScreenReader);
            return new ZhengduChannel(config, ZhengduChannelMode.Independent);
        }

    }
}
