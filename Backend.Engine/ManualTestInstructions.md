# Manual Debugger Auto-Attach Verification

1. Ensure `appsettings.Development.json` contains the correct Visual Studio solution paths for your plugin repositories under `DebuggerAutoAttach:SolutionPaths`.
2. Start Visual Studio and open one of the configured plugin solutions (or close Visual Studio if you want the fallback launch behavior).
3. Run the Engine project (`Backend.Engine`) under the Visual Studio debugger so that the API host is running in Development mode.
4. Trigger a plugin runtime invocation so the engine starts a child process (for example, enqueue a process step that uses a plugin).
5. Observe the engine logs: when the child process starts, the engine should log attempts to attach to a matching Visual Studio instance and report success once attached.
6. If no instance was open and fallback launch is enabled, confirm Visual Studio starts with the configured solution and then attaches to the child process.
7. Verify the correct Visual Studio instance shows the plugin runtime process in the Debugged Processes list after attachment.
