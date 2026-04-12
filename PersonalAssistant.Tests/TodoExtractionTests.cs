using System;
using System.Reflection;
using Xunit;

namespace PersonalAssistant.Tests
{
    public class TodoExtractionTests
    {
        [Fact]
        public void ExtractTodoData_PutsFullMessageInDescription_WhenTitleWouldBeFullMessage()
        {
            var type = typeof(TelegramMessageHandler);
            var method = type.GetMethod("ExtractTodoDataFromText", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.NotNull(method);

            var text = "Can we add a to do to add a clear button on this form";
            var result = method!.Invoke(null, new object[] { text });
            Assert.NotNull(result);

            var todoType = result.GetType();
            var titleProp = todoType.GetProperty("Title", BindingFlags.Public | BindingFlags.Instance);
            var descProp = todoType.GetProperty("Description", BindingFlags.Public | BindingFlags.Instance);
            Assert.NotNull(titleProp);
            Assert.NotNull(descProp);

            var title = (string?)titleProp!.GetValue(result);
            var description = (string?)descProp!.GetValue(result);

            Assert.Equal(text.Trim(), description);
            Assert.NotEqual(description, title);
            Assert.True((title?.Length ?? 0) <= 60);
        }

        [Fact]
        public void GitHubTodosService_ReadsAutoCreateFlag_FromEnvironment()
        {
            try
            {
                Environment.SetEnvironmentVariable("GITHUB_PERSONAL_TODOS_TOKEN", "dummy-token");
                Environment.SetEnvironmentVariable("GITHUB_TODOS_REPO", "owner/repo");
                Environment.SetEnvironmentVariable("GITHUB_TODOS_AUTO_CREATE", "false");

                var svc = GitHubTodosService.FromEnvironment();
                Assert.False(svc.AutoCreateEnabled);

                Environment.SetEnvironmentVariable("GITHUB_TODOS_AUTO_CREATE", "true");
                var svc2 = GitHubTodosService.FromEnvironment();
                Assert.True(svc2.AutoCreateEnabled);
            }
            finally
            {
                Environment.SetEnvironmentVariable("GITHUB_PERSONAL_TODOS_TOKEN", null);
                Environment.SetEnvironmentVariable("GITHUB_TODOS_REPO", null);
                Environment.SetEnvironmentVariable("GITHUB_TODOS_AUTO_CREATE", null);
            }
        }
    }
}
