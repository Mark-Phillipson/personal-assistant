using System;
using System.Linq;
using System.Reflection;
using Xunit;

namespace PersonalAssistant.Tests
{
    public class TelegramAudioTests
    {
        private static bool InvokeShouldSendTelegramAudio(string? input)
        {
            var assembly = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetTypes().Any(t => t.Name == "TelegramMessageHandler"));

            if (assembly == null)
                throw new InvalidOperationException("Could not find TelegramMessageHandler assembly in current AppDomain.");

            var handlerType = assembly.GetTypes().First(t => t.Name == "TelegramMessageHandler");
            var method = handlerType.GetMethod("ShouldSendTelegramAudio", BindingFlags.NonPublic | BindingFlags.Static);
            if (method == null)
                throw new InvalidOperationException("ShouldSendTelegramAudio method not found on TelegramMessageHandler.");

            return (bool)method.Invoke(null, new object[] { input });
        }

        [Fact]
        public void ShouldSendTelegramAudio_ReturnsFalse_WhenNoRequest()
        {
            Assert.False(InvokeShouldSendTelegramAudio(null));
            Assert.False(InvokeShouldSendTelegramAudio(string.Empty));
            Assert.False(InvokeShouldSendTelegramAudio("Hello, how are you?"));
        }

        [Fact]
        public void ShouldSendTelegramAudio_ReturnsTrue_WhenExplicitAudioRequested()
        {
            Assert.True(InvokeShouldSendTelegramAudio("Please reply as audio"));
            Assert.True(InvokeShouldSendTelegramAudio("Send as voice"));
            Assert.True(InvokeShouldSendTelegramAudio("Could you read that out loud?"));
        }

        [Fact]
        public void ShouldSendTelegramAudio_ReturnsFalse_WhenTextRepresentationRequested()
        {
            Assert.False(InvokeShouldSendTelegramAudio("Send audio as text"));
            Assert.False(InvokeShouldSendTelegramAudio("wav as text please"));
            Assert.False(InvokeShouldSendTelegramAudio("audio transcript please"));
        }
    }
}
