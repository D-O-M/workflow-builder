# Attach Visual Studio to a running Plugin

Use this procedure on Windows to inspect a Plugin executing a Step. You need Visual Studio, the Plugin source and symbols matching the bundle that Engine loads. [Local startup and bundle commands](../Docs/agent-context/developer-workflows.md)

1. Open the Plugin source in Visual Studio and set a breakpoint in its task implementation.
2. Start WorkflowBuilder locally. In the Engine logs, find the entry for the intended Plugin version and its child process ID (`Process started; ID`). Wait for the corresponding `Plugin agent ready` entry.
3. In Visual Studio, choose **Debug → Attach to Process**, select the local child PID from the log and attach the managed .NET debugger. Confirm that the breakpoint binds to the loaded symbols.
4. Start a Process whose Blueprint uses that runnable task version. When the breakpoint hits, inspect the invocation and continue execution. Check the Step result in Process Explorer. If Engine reloads or restarts the agent, repeat the attachment with its new PID.

Engine launches the child at catalog startup or reload and sends later Step jobs to that process. The `VisualStudioDebuggerAttacher` helper has isolated tests. The current Plugin startup path records the PID and leaves attachment to the developer. [Agent startup](PluginRuntimeOrchestrator.cs) · [Child process launch](PluginRuntimeAgent.cs) · [Debugger helper tests](../Backend.Engine.Tests/Debugging/VisualStudioDebuggerAttacherTests.cs)
