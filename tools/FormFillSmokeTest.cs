using System;
using System.Threading.Tasks;
using System.IO;

#pragma warning disable CS7022
class FormFillSmokeTest
{
    static async Task<int> Main(string[] args)
    {
        try
        {
            // Find repository root by walking up until the solution file exists
            string? repoRoot = Directory.GetCurrentDirectory();
            while (repoRoot != null && !File.Exists(Path.Combine(repoRoot, "personal-assistant.sln")))
            {
                var parent = Directory.GetParent(repoRoot);
                repoRoot = parent?.FullName;
            }
            if (repoRoot is null)
            {
                repoRoot = Directory.GetCurrentDirectory(); // fallback
            }

            var candidates = new[]
            {
                Path.Combine(repoRoot, "bin", "Debug", "net10.0", "personal-assistant.dll"),
                Path.Combine(repoRoot, "bin", "Debug", "net10.0", "win-x64", "personal-assistant.dll"),
                Path.Combine(repoRoot, "bin", "Debug", "net10.0-verify", "personal-assistant.dll"),
                Path.Combine(repoRoot, "personal-assistant", "bin", "Debug", "net10.0", "personal-assistant.dll"),
                Path.Combine(repoRoot, "personal-assistant", "bin", "Debug", "net10.0", "win-x64", "personal-assistant.dll")
            };

            string? asmPath = null;
            foreach (var c in candidates)
            {
                var full = Path.GetFullPath(c);
                if (File.Exists(full)) { asmPath = full; break; }
            }

            if (asmPath is null)
            {
                Console.WriteLine("Could not find built assembly in known locations. Please run a normal 'dotnet build' for the main project and try again.");
                Console.WriteLine("Searched: ");
                foreach (var c in candidates) Console.WriteLine("  " + Path.GetFullPath(c));
                return 2;
            }

            var asm = System.Reflection.Assembly.LoadFrom(asmPath);

            // Find a candidate type that exposes the expected factory or methods
            var types = asm.GetTypes();
            Type? type = null;
            // Prefer explicitly named type
            type = types.FirstOrDefault(t => string.Equals(t.Name, "WebBrowserAssistantService", StringComparison.OrdinalIgnoreCase));
            if (type == null)
            {
                // Prefer types that expose both Read and Fill methods
                type = types.FirstOrDefault(t => t.GetMethod("ReadWebFormStructureAsync") != null && t.GetMethod("FillWebFormAsync") != null);
            }
            if (type == null)
            {
                // Fallback: any type with a FromEnvironment static factory
                type = types.FirstOrDefault(t => t.GetMethod("FromEnvironment", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static) != null);
            }

            if (type is null)
            {
                Console.WriteLine("Could not find a type exposing the WebBrowserAssistantService methods in the assembly.");
                return 2;
            }

            Console.WriteLine("Discovered service type: " + type.FullName);

            object? svc = null;
            var fromEnv = type.GetMethod("FromEnvironment", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
            if (fromEnv != null)
            {
                var parms = fromEnv.GetParameters();
                var invokeArgs = new object?[parms.Length];
                // Fill with nulls; many factory patterns accept optional config or environment parameters
                for (int i = 0; i < parms.Length; i++) invokeArgs[i] = null;
                svc = fromEnv.Invoke(null, invokeArgs);
            }
            else
            {
                // Try parameterless constructor
                var ctor = type.GetConstructor(Type.EmptyTypes);
                if (ctor != null)
                {
                    svc = Activator.CreateInstance(type!);
                }
            }

            if (svc is null)
            {
                Console.WriteLine("Failed to instantiate service type.");
                return 2;
            }

            var readMethod = type.GetMethod("ReadWebFormStructureAsync");
            var fillMethod = type.GetMethod("FillWebFormAsync");
            var screenshotMethod = type.GetMethod("TakePageScreenshotAsync");

            Console.WriteLine("Methods present on type:");
            foreach (var m in type.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static))
            {
                Console.WriteLine(" - " + m.Name);
            }

            var url = args.Length > 0 ? args[0] : "http://localhost:5000/customintellisense";

            if (readMethod is null)
            {
                Console.WriteLine("ReadWebFormStructureAsync not available in this build — skipping discovery step.");
            }
            else
            {
                Console.WriteLine($"Reading form structure at {url}...");

                var readTaskObj = readMethod.Invoke(svc, new object[] { url, System.Threading.CancellationToken.None });
                var readTask = readTaskObj as Task<string>;
                if (readTask != null)
                {
                    var result = await readTask.ConfigureAwait(false);
                    Console.WriteLine("-- ReadWebFormStructureAsync result --\n" + result + "\n-- end --");
                }
                else
                {
                    Console.WriteLine("Read method did not return Task<string> as expected; continuing to fill step.");
                }
            }

            // Sample JSON — update fields to target your page's form names/ids as needed
            var json = "{\"firstName\":\"Test\",\"lastName\":\"User\",\"email\":\"test@example.com\"}";

            Console.WriteLine("Calling FillWebFormAsync (no submit)...");
            var fillTaskObj = fillMethod.Invoke(svc, new object[] { url, json, false, System.Threading.CancellationToken.None });
            var fillTask = fillTaskObj as Task<string>;
            if (fillTask == null)
            {
                Console.WriteLine("Fill method did not return Task<string> as expected.");
                return 2;
            }
            var fillResult = await fillTask.ConfigureAwait(false);
            Console.WriteLine("-- FillWebFormAsync result --\n" + fillResult + "\n-- end --");

            Console.WriteLine("Taking screenshot...");
            if (screenshotMethod != null)
            {
                var ssTaskObj = screenshotMethod.Invoke(svc, new object[] { url, System.Threading.CancellationToken.None });
                var ssTask = ssTaskObj as Task<string>;
                if (ssTask != null)
                {
                    var ssPath = await ssTask.ConfigureAwait(false);
                    Console.WriteLine("Screenshot saved: " + ssPath);
                }
            }

            // Try conversational fill if available
            var convMethod = type.GetMethod("ConversationalFillAsync");
            if (convMethod != null)
            {
                Console.WriteLine("Calling ConversationalFillAsync with a sample instruction...");
                var convTaskObj = convMethod.Invoke(svc, new object[] { url, "put Test User in the first name field", false, System.Threading.CancellationToken.None });
                var convTask = convTaskObj as Task<string>;
                if (convTask != null)
                {
                    var convResult = await convTask.ConfigureAwait(false);
                    Console.WriteLine("-- ConversationalFillAsync result --\n" + convResult + "\n-- end --");
                }
            }

            // Dispose if IAsyncDisposable
            if (svc is IAsyncDisposable ad)
            {
                await ad.DisposeAsync();
            }

            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine("Exception: " + ex);
            return 3;
        }
    }
}
