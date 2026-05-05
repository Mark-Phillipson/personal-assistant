using System;
using System.Linq;
using System.Reflection;
using Xunit;

namespace PersonalAssistant.Tests
{
    public class CalendarFormattingTests
    {
        private static string InvokeNormalizeEventText(string? input)
        {
            var assembly = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetTypes().Any(t => t.Name == "TelegramMessageHandler"));

            if (assembly == null)
                throw new InvalidOperationException("Could not find TelegramMessageHandler assembly in current AppDomain.");

            var handlerType = assembly.GetTypes().First(t => t.Name == "TelegramMessageHandler");
            var method = handlerType.GetMethod("NormalizeEventText", BindingFlags.NonPublic | BindingFlags.Static);
            if (method == null)
                throw new InvalidOperationException("NormalizeEventText method not found on TelegramMessageHandler.");

            return (string)method.Invoke(null, new object?[] { input })!;
        }

        [Fact]
        public void NormalizeEventText_RemovesHtmlEntitiesAndTags()
        {
            var input1 = "&lt;b&gt;Get ready — Fable user interview&lt;/b&gt;";
            var out1 = InvokeNormalizeEventText(input1);
            Assert.DoesNotContain("&lt;", out1);
            Assert.DoesNotContain("&gt;", out1);
            Assert.DoesNotContain("<", out1);
            Assert.DoesNotContain(">", out1);
            Assert.Equal("Get ready — Fable user interview", out1);

            var input2 = "<b>Bolded title</b>";
            var out2 = InvokeNormalizeEventText(input2);
            Assert.Equal("Bolded title", out2);

            var input3 = "Plain text";
            var out3 = InvokeNormalizeEventText(input3);
            Assert.Equal("Plain text", out3);
        }
    }
}
