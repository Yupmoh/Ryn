using FluentAssertions;
using NSubstitute;
using Ryn.Core;
using Ryn.Ipc;
using Ryn.Plugins.Shell;
using Xunit;

namespace Ryn.Plugins.Tests;

public sealed class ShellCommandsTests
{
    private sealed class UnixFactAttribute : FactAttribute
    {
        public UnixFactAttribute()
        {
            if (OperatingSystem.IsWindows())
                Skip = "Unix native PTY fixture.";
        }
    }

    private static ShellExecutionPolicy Policy(ShellOptions options) => new(options);

    private static ShellCommands Shell(ShellOptions options) => new(new ShellExecutionPolicy(options));

#pragma warning disable CA2012 // NSubstitute configures ValueTask return values; these calls do not start window operations.
    [UnixFact]
    public async Task Terminal_ClosedWindow_RevokesAttachmentsAndWakesReaders()
    {
        using var service = TerminalService();
        var created = service.Create("/bin/sh", "[\"-c\",\"sleep 30\"]",
            retentionMode: TerminalRetentionMode.Reattachable);
        var alreadyClosed = Substitute.For<IRynWindow>();
        alreadyClosed.Id.Returns(10);
        SubstituteExtensions.Returns<ValueTask>(alreadyClosed.WaitForCloseAsync(), ValueTask.CompletedTask);
        var lateAttach = () => service.AttachForWindow(created.SessionId, created.AccessToken,
            alreadyClosed, "0", 0, TerminalAttachmentMode.Controller);
        lateAttach.Should().Throw<TerminalProtocolException>();
        var window = Substitute.For<IRynWindow>();
        window.Id.Returns(11);
        SubstituteExtensions.Returns<ValueTask>(window.WaitForCloseAsync(), new ValueTask(new TaskCompletionSource().Task));
        var attached = service.AttachForWindow(created.SessionId, created.AccessToken,
            window, "0", 0, TerminalAttachmentMode.Controller);
        var read = service.ReadAsync(created.SessionId, attached.AttachmentId,
            attached.Generation, 11).AsTask();
        window.Closed += Raise.Event<EventHandler>(window, EventArgs.Empty);
        (await read.WaitAsync(TimeSpan.FromSeconds(3))).Error.Should().Be("unauthorized_attachment");
        var reattach = () => service.AttachForWindow(created.SessionId, created.AccessToken,
            window, "0", 0, TerminalAttachmentMode.Controller);
        reattach.Should().Throw<TerminalProtocolException>();
        var replacement = Substitute.For<IRynWindow>();
        replacement.Id.Returns(12);
        SubstituteExtensions.Returns<ValueTask>(replacement.WaitForCloseAsync(), new ValueTask(new TaskCompletionSource().Task));
        var next = service.AttachForWindow(created.SessionId, created.AccessToken,
            replacement, "0", 0, TerminalAttachmentMode.Controller);
        service.Terminate(created.SessionId, next.AttachmentId, next.Generation, 12).Ok.Should().BeTrue();
    }
#pragma warning restore CA2012

    [Fact]
    public void ScopedCommand_PermitsMatchingArgs_RejectsOthers()
    {
        // echo is scoped to exactly one literal argument "hello".
        var shell = Shell(new ShellOptions
        {
            CommandScopes = [new CommandScope("echo", [ArgRule.Literal("hello")])],
        });

        var ok = () => shell.Execute("echo", "[\"hello\"]");
        ok.Should().NotThrow();

        var wrongArg = () => shell.Execute("echo", "[\"goodbye\"]");
        wrongArg.Should().Throw<UnauthorizedAccessException>();

        var wrongCount = () => shell.Execute("echo", "[\"hello\", \"world\"]");
        wrongCount.Should().Throw<UnauthorizedAccessException>();
    }

    [Fact]
    public void ScopedCommand_RegexValidator_Enforced()
    {
        var shell = Shell(new ShellOptions
        {
            CommandScopes = [new CommandScope("echo", [ArgRule.Pattern("^[a-z]+$")])],
        });

        ((Action)(() => shell.Execute("echo", "[\"abc\"]"))).Should().NotThrow();
        ((Action)(() => shell.Execute("echo", "[\"abc123\"]"))).Should().Throw<UnauthorizedAccessException>();
        // injection attempt is just a non-matching argument, passed literally (never to a shell)
        ((Action)(() => shell.Execute("echo", "[\"$(rm -rf /)\"]"))).Should().Throw<UnauthorizedAccessException>();
    }

    [Fact]
    public void Execute_FailsClosed_WhenNotConfigured()
    {
        // An empty allowlist is the reachable production "nothing permitted" state.
        var shell = Shell(new ShellOptions { AllowedCommands = [] });
        ((Action)(() => shell.Execute("echo", "[]"))).Should().Throw<UnauthorizedAccessException>();
    }

    [Fact]
    public void Open_RejectsFileScheme()
    {
        var policy = Policy(new ShellOptions());
        ((Action)(() => policy.ValidateOpenTarget("file:///etc/passwd"))).Should().Throw<UnauthorizedAccessException>();
    }

    [Fact]
    public void Open_RejectsBarePath()
    {
        var policy = Policy(new ShellOptions());
        ((Action)(() => policy.ValidateOpenTarget("/Applications/Calculator.app"))).Should().Throw<UnauthorizedAccessException>();
        ((Action)(() => policy.ValidateOpenTarget("Calculator.app"))).Should().Throw<UnauthorizedAccessException>();
    }

    [Fact]
    public void Open_AllowsHttpsByDefault()
    {
        var policy = Policy(new ShellOptions());
        ((Action)(() => policy.ValidateOpenTarget("https://example.com"))).Should().NotThrow();
        ((Action)(() => policy.ValidateOpenTarget("mailto:a@b.com"))).Should().NotThrow();
    }

    [Fact]
    public void Open_HonorsConfiguredSchemeAllowlist()
    {
        var policy = Policy(new ShellOptions { AllowedOpenSchemes = ["https"] });
        ((Action)(() => policy.ValidateOpenTarget("https://example.com"))).Should().NotThrow();
        ((Action)(() => policy.ValidateOpenTarget("mailto:a@b.com"))).Should().Throw<UnauthorizedAccessException>();
    }

    [Fact]
    public void Execute_DeniedWhenAllowlistEmpty()
    {
        var shell = Shell(new ShellOptions { AllowedCommands = [] });

        var act = () => shell.Execute("ls", "[]");
        act.Should().Throw<UnauthorizedAccessException>();
    }

    [Fact]
    public void Execute_AllowedCommand_ReturnsOutput()
    {
        var shell = Shell(new ShellOptions { AllowedCommands = ["echo"] });

        var result = shell.Execute("echo", "[\"hello\"]");
        result.Should().Contain("hello");
        result.Should().Contain("exitCode");
    }

    [Fact]
    public void Execute_DeniedCommand_Throws()
    {
        var shell = Shell(new ShellOptions { AllowedCommands = ["echo"] });

        var act = () => shell.Execute("rm", "[\"-rf\", \"/\"]");
        act.Should().Throw<UnauthorizedAccessException>();
    }

    [Fact]
    public void Execute_HonorsTimeout_KillsAndThrows()
    {
        // A long-running command with a tiny timeout must be terminated and surfaced as a timeout
        // rather than hanging. On Windows `timeout` exits immediately when stdin is redirected (as it
        // is under Process), so it never blocks long enough — use `ping` as the long-runner instead.
        var shell = Shell(new ShellOptions
        {
            AllowedCommands = OperatingSystem.IsWindows() ? ["ping"] : ["sleep"],
            ExecuteTimeout = TimeSpan.FromMilliseconds(250),
        });

        var (command, args) = OperatingSystem.IsWindows()
            ? ("ping", "[\"-n\", \"30\", \"127.0.0.1\"]")
            : ("sleep", "[\"30\"]");
        var act = () => shell.Execute(command, args);
        act.Should().Throw<TimeoutException>();
    }

    [Fact]
    public void Spawn_RejectsDisallowedCommand()
    {
        var policy = Policy(new ShellOptions { AllowedCommands = ["echo"] });
        var webView = Substitute.For<IRynWebView>();
        using var commands = new SpawnCommands(webView, policy);

        var act = () => commands.Spawn("rm", "[\"-rf\", \"/\"]");
        act.Should().Throw<UnauthorizedAccessException>();
    }

    [Fact]
    public void Kill_ReturnsErrorForUnknownPid()
    {
        var policy = Policy(new ShellOptions { AllowedCommands = ["echo"] });
        var webView = Substitute.For<IRynWebView>();
        using var commands = new SpawnCommands(webView, policy);

        var result = commands.Kill(99999);
        result.Should().BeFalse();
    }
    [Fact]
    public void Pty_RejectsInvalidInitialDimensionsBeforeSpawn()
    {
        var policy = Policy(new ShellOptions { AllowedCommands = ["echo"] });
        var webView = Substitute.For<IRynWebView>();
        using var commands = new PtyCommands(webView, policy);

        var act = () => commands.Pty("echo", "[]", 0, 24);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void PtySignal_ReturnsFalseForUnknownSession()
    {
        var policy = Policy(new ShellOptions { AllowedCommands = ["echo"] });
        var webView = Substitute.For<IRynWebView>();
        using var commands = new PtyCommands(webView, policy);

        commands.PtySignal(99999, 15).Should().BeFalse();
    }

    [Fact]
    public void BuildProcessEnvironment_ScrubsSecrets()
    {
        const string visibleKey = "RYN_PTY_VISIBLE_TEST";
        const string secretKey = "RYN_PTY_TOKEN_TEST";
        var previousVisible = Environment.GetEnvironmentVariable(visibleKey);
        var previousSecret = Environment.GetEnvironmentVariable(secretKey);
        try
        {
            Environment.SetEnvironmentVariable(visibleKey, "visible");
            Environment.SetEnvironmentVariable(secretKey, "secret");
            var policy = Policy(new ShellOptions());

            var environment = policy.BuildProcessEnvironment();

            environment.Should().Contain(visibleKey, "visible");
            environment.Should().NotContainKey(secretKey);
        }
        finally
        {
            Environment.SetEnvironmentVariable(visibleKey, previousVisible);
            Environment.SetEnvironmentVariable(secretKey, previousSecret);
        }
    }

    [Fact]
    public void BuildProcessEnvironment_CanDisableInheritance()
    {
        var policy = Policy(new ShellOptions { InheritEnvironment = false });

        policy.BuildProcessEnvironment().Should().BeEmpty();
    }

    [Fact]
    public void WindowsEnvironmentBlock_IsSortedAndDoubleNullTerminated()
    {
        var block = PtyNativeWindows.BuildEnvironmentBlock(new Dictionary<string, string>
        {
            ["Z_VAR"] = "last",
            ["A_VAR"] = "first",
        });

        new string(block).Should().Be("A_VAR=first\0Z_VAR=last\0\0");
    }

    [Fact]
    public void Pty_UnixSession_StreamsOutputAndExit()
    {
        if (OperatingSystem.IsWindows())
            return;

        var policy = Policy(new ShellOptions { AllowedCommands = ["/bin/sh"] });
        var webView = Substitute.For<IRynWebView>();
        var events = new System.Collections.Concurrent.ConcurrentQueue<(string Name, string Json)>();
        webView.When(view => view.EmitEvent(Arg.Any<string>(), Arg.Any<string>()))
            .Do(call => events.Enqueue((call.ArgAt<string>(0), call.ArgAt<string>(1))));
        using var commands = new PtyCommands(webView, policy);

        var sessionId = commands.Pty("/bin/sh", "[\"-c\",\"printf RYN_PTY_OK; stty size\"]", 91, 37);
        var outputEvent = $"shell.pty.stdout.{sessionId}";
        var exitEvent = $"shell.pty.exit.{sessionId}";

        SpinWait.SpinUntil(
            () => events.Any(entry => entry.Name == exitEvent),
            TimeSpan.FromSeconds(5)).Should().BeTrue();

        var encodedChunks = events
            .Where(entry => entry.Name == outputEvent)
            .SelectMany(entry => System.Text.Json.JsonSerializer.Deserialize<string[]>(entry.Json) ?? []);
        var output = string.Concat(encodedChunks.Select(chunk =>
            System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(chunk))));
        output.Should().Contain("RYN_PTY_OK");
        output.Should().Contain("37 91");
        events.Should().Contain(entry => entry.Name == exitEvent && entry.Json == "0");
    }

    [Fact]
    public async Task Pty_Kill_UnblocksSaturatedNativeWriter()
    {
        if (OperatingSystem.IsWindows()) return;
        using var service = TerminalService();
        var native = service.SpawnNative("/bin/sh",
            "[\"-c\",\"stty raw -echo; printf READY; exec sleep 30\"]", 80, 24);
        Task<bool>? writing = null;
        try
        {
            var ready = new byte[64];
            var count = native.Read(ready, ready.Length, 2000);
            count.Should().BeGreaterThan(0);
            System.Text.Encoding.UTF8.GetString(ready, 0, count).Should().Contain("READY");
            writing = Task.Run(() => native.Write(new byte[8 * 1024 * 1024]));
            await Task.Delay(200);
            writing.IsCompleted.Should().BeFalse("the child never reads raw stdin");
            native.RequestKill();
            (await writing.WaitAsync(TimeSpan.FromSeconds(5))).Should().BeFalse();
        }
        finally
        {
            native.RequestKill();
            if (writing is not null) await writing.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Run(() => native.WaitForExit()).WaitAsync(TimeSpan.FromSeconds(5));
            native.CompleteLifetime();
        }
    }

    [Fact]
    public async Task Terminal_RepeatedAttach_ReplaysContiguousPrefixWhileOutputContinues()
    {
        if (OperatingSystem.IsWindows()) return;
        using var service = TerminalService();
        var created = service.Create("/bin/sh",
            "[\"-c\",\"i=0; while [ $i -lt 80 ]; do printf '%03d,' $i; i=$((i+1)); sleep 0.01; done\"]",
            retentionMode: TerminalRetentionMode.Reattachable);
        var expected = string.Concat(Enumerable.Range(0, 80).Select(i => i.ToString("D3", System.Globalization.CultureInfo.InvariantCulture) + ","));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        for (var cycle = 0; cycle < 3; cycle++)
        {
            var attached = service.Attach(created.SessionId, created.AccessToken, 11, "0", 16);
            var output = new System.Text.StringBuilder();
            var offset = "0";
            while (true)
            {
                var packet = await service.ReadAsync(created.SessionId, attached.AttachmentId, attached.Generation,
                    11, 16, 1000, timeout.Token);
                packet.Error.Should().BeNull();
                packet.StartOffset.Should().Be(offset);
                offset = packet.EndOffset;
                output.Append(System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(packet.DataBase64)));
                service.Ack(created.SessionId, attached.AttachmentId, attached.Generation, 11, offset).Ok.Should().BeTrue();
                if (cycle < 2 && output.Length >= 32) break;
                if (packet.ProcessExited && packet.FinalOffset == offset) break;
            }
            expected.Should().StartWith(output.ToString());
            if (cycle == 2) output.ToString().Should().Be(expected);
            service.Detach(created.SessionId, attached.AttachmentId, attached.Generation, 11).Ok.Should().BeTrue();
        }
    }

    [Fact]
    public async Task Terminal_ExitedRetention_PreservesOutputThenReclaimsSessionSlot()
    {
        if (OperatingSystem.IsWindows()) return;
        using var service = TerminalService(new TerminalOptions
        {
            MaxSessions = 1,
            ExitedSessionRetention = TimeSpan.FromSeconds(2),
        });
        var created = service.Create("/bin/sh", "[\"-c\",\"printf retained\"]",
            retentionMode: TerminalRetentionMode.Reattachable);
        var attached = service.Attach(created.SessionId, created.AccessToken, 11, "0");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var output = new System.Text.StringBuilder();
        while (true)
        {
            var packet = await service.ReadAsync(created.SessionId, attached.AttachmentId, attached.Generation, 11,
                cancellationToken: timeout.Token);
            output.Append(System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(packet.DataBase64)));
            service.Ack(created.SessionId, attached.AttachmentId, attached.Generation, 11, packet.EndOffset);
            if (packet.ProcessExited && packet.FinalOffset == packet.EndOffset) break;
        }
        output.ToString().Should().Be("retained");
        service.Detach(created.SessionId, attached.AttachmentId, attached.Generation, 11);
        var replay = service.Attach(created.SessionId, created.AccessToken, 12, "0");
        var retained = await service.ReadAsync(created.SessionId, replay.AttachmentId, replay.Generation, 12);
        System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(retained.DataBase64)).Should().Be("retained");
        await Task.Delay(2100, timeout.Token);
        var replacement = service.Create("/bin/sh", "[\"-c\",\"sleep 1\"]");
        replacement.SessionId.Should().NotBe(created.SessionId);
        var expired = () => service.Attach(created.SessionId, created.AccessToken, 11, "0");
        expired.Should().Throw<TerminalProtocolException>();
    }

    [Fact]
    public void Terminal_DefaultBudgets_AreFiniteAndConsistent()
    {
        var options = new TerminalOptions();

        options.AttachmentCreditBytes.Should().Be(256 * 1024);
        options.SessionRetentionBytes.Should().Be(8 * 1024 * 1024);
        options.GlobalRetentionBytes.Should().Be(64L * 1024 * 1024);
        options.MaxReadBytes.Should().BeLessThanOrEqualTo(options.AttachmentCreditBytes);
        options.MaxInputItems.Should().Be(64);
    }

    [Fact]
    public async Task Terminal_AcknowledgedStream_IsByteExactAndRejectsFutureAck()
    {
        if (OperatingSystem.IsWindows()) return;
        using var service = TerminalService(new TerminalOptions
        {
            AttachmentCreditBytes = 32,
            SessionRetentionBytes = 256,
            GlobalRetentionBytes = 1024,
            MaxReadBytes = 16,
        });
        var created = service.Create("/bin/sh", "[\"-c\",\"printf 0123456789abcdefghijklmnopqrstuvwxyz\"]", 80, 24);
        var attached = service.Attach(created.SessionId, created.AccessToken, 11, "0", 32);

        var bytes = new List<byte>();
        var offset = "0";
        while (true)
        {
            var read = await service.ReadAsync(created.SessionId, attached.AttachmentId, attached.Generation, 11, 16, 1000);
            read.Error.Should().BeNull();
            read.StartOffset.Should().Be(offset);
            bytes.AddRange(Convert.FromBase64String(read.DataBase64));
            offset = read.EndOffset;
            service.Ack(created.SessionId, attached.AttachmentId, attached.Generation, 11, offset).Ok.Should().BeTrue();
            if (read.ProcessExited && read.EndOffset == read.FinalOffset) break;
        }
        var duplicate = service.Ack(created.SessionId, attached.AttachmentId, attached.Generation, 11, offset);
        duplicate.Ok.Should().BeTrue();

        System.Text.Encoding.UTF8.GetString([.. bytes]).Should().Be("0123456789abcdefghijklmnopqrstuvwxyz");
        var future = (ulong.Parse(offset, System.Globalization.CultureInfo.InvariantCulture) + 1)
            .ToString(System.Globalization.CultureInfo.InvariantCulture);
        service.Ack(created.SessionId, attached.AttachmentId, attached.Generation, 11, future).Error
            .Should().Be("invalid_offset");
    }

    [Fact]
    public async Task Terminal_WithheldAck_BlocksDeliveryWithoutBlockingAnotherSession()
    {
        if (OperatingSystem.IsWindows()) return;
        using var service = TerminalService(new TerminalOptions
        {
            AttachmentCreditBytes = 16,
            MaxReadBytes = 16,
            SessionRetentionBytes = 256,
            GlobalRetentionBytes = 1024,
        });
        var created = service.Create("/bin/sh", "[\"-c\",\"printf 0123456789abcdefghijklmnopqrstuv; sleep 30\"]");
        var owner = service.Attach(created.SessionId, created.AccessToken, 11, "0", 16);
        TerminalReadResult first;
        using var initialDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        do
        {
            first = await service.ReadAsync(created.SessionId, owner.AttachmentId, owner.Generation, 11, 16, 1000, initialDeadline.Token);
            first.Error.Should().BeNull();
        } while (first.EndOffset != "16" && first.FinalOffset is null);
        first.EndOffset.Should().Be("16");
        service.Ack(created.SessionId, owner.AttachmentId, owner.Generation, 11, "32").Error.Should().Be("invalid_offset");
        var stalled = await service.ReadAsync(created.SessionId, owner.AttachmentId, owner.Generation, 11, 16, 50);
        stalled.DataBase64.Should().BeEmpty();
        stalled.EndOffset.Should().Be("16");
        var other = service.Create("/bin/sh", "[\"-c\",\"printf independent\"]");
        var viewer = service.Attach(other.SessionId, other.AccessToken, 12, "0", 16);
        TerminalReadResult independent;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        do
        {
            independent = await service.ReadAsync(other.SessionId, viewer.AttachmentId, viewer.Generation, 12, 16, 1000);
            independent.Error.Should().BeNull();
            if (independent.DataBase64.Length != 0 || independent.FinalOffset is not null) break;
            await Task.Delay(10);
        } while (DateTime.UtcNow < deadline);
        System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(independent.DataBase64)).Should().Be("independent");
        service.Ack(created.SessionId, owner.AttachmentId, owner.Generation, 11, first.EndOffset).Ok.Should().BeTrue();
        var resumed = await service.ReadAsync(created.SessionId, owner.AttachmentId, owner.Generation, 11, 16, 1000);
        System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(resumed.DataBase64)).Should().Be("ghijklmnopqrstuv");
        resumed.StartOffset.Should().Be(first.EndOffset);
    }

    [Fact]
    public async Task Terminal_AttachmentIdentityAndGeneration_AreHostAuthorized()
    {
        if (OperatingSystem.IsWindows()) return;
        using var service = TerminalService();
        var created = service.Create("/bin/sh", "[\"-c\",\"sleep 5\"]");
        var attached = service.Attach(created.SessionId, created.AccessToken, 21, "0");

        var wrongWindow = await service.ReadAsync(
            created.SessionId, attached.AttachmentId, attached.Generation, 22, 16, 0);
        wrongWindow.Error.Should().Be("unauthorized_attachment");
        service.Ack(created.SessionId, attached.AttachmentId, "999", 21, "0").Error
            .Should().Be("stale_generation");
        service.Terminate(created.SessionId, attached.AttachmentId, attached.Generation, 21).Ok.Should().BeTrue();
    }


    [Fact]
    public async Task Terminal_ReplayFromCheckpointBoundary_IsContiguous()
    {
        if (OperatingSystem.IsWindows()) return;
        using var service = TerminalService(new TerminalOptions
        {
            AttachmentCreditBytes = 64,
            SessionRetentionBytes = 1024,
            GlobalRetentionBytes = 4096,
            MaxReadBytes = 64,
        });
        var created = service.Create(
            "/bin/sh", "[\"-c\",\"printf before; sleep 0.2; printf after\"]", retentionMode: TerminalRetentionMode.Reattachable);
        var first = service.Attach(created.SessionId, created.AccessToken, 31, "0", 64);
        var before = await service.ReadAsync(created.SessionId, first.AttachmentId, first.Generation, 31, 64, 1000);
        var checkpointOffset = before.EndOffset;
        service.Ack(created.SessionId, first.AttachmentId, first.Generation, 31, checkpointOffset).Ok.Should().BeTrue();
        service.StoreCheckpoint(
            created.SessionId, first.AttachmentId, first.Generation, 31, checkpointOffset,
            "xterm-addon-serialize@0.14.0:text-v1", Convert.ToBase64String("screen"u8), 80, 24).Ok.Should().BeTrue();
        service.Detach(created.SessionId, first.AttachmentId, first.Generation, 31).Ok.Should().BeTrue();

        var second = service.Attach(created.SessionId, created.AccessToken, 31, checkpointOffset, 64);
        second.Checkpoint.Should().NotBeNull();
        second.Checkpoint!.Offset.Should().Be(checkpointOffset);
        var tailBytes = new List<byte>();
        var tailOffset = checkpointOffset;
        while (true)
        {
            var tail = await service.ReadAsync(created.SessionId, second.AttachmentId, second.Generation, 31, 64, 1000);
            tail.StartOffset.Should().Be(tailOffset);
            tailBytes.AddRange(Convert.FromBase64String(tail.DataBase64));
            tailOffset = tail.EndOffset;
            service.Ack(created.SessionId, second.AttachmentId, second.Generation, 31, tailOffset).Ok.Should().BeTrue();
            if (tail.ProcessExited && tail.EndOffset == tail.FinalOffset) break;
        }
        System.Text.Encoding.UTF8.GetString([.. tailBytes]).Should().Contain("after");
    }

    [Fact]
    public async Task Terminal_CheckpointReplacement_UsesBytesPrunedBySameCommit()
    {
        if (OperatingSystem.IsWindows()) return;
        using var service = TerminalService(new TerminalOptions
        {
            AttachmentCreditBytes = 1024,
            SessionRetentionBytes = 1024,
            GlobalRetentionBytes = 1024,
            MaxReadBytes = 1024,
            MaxCheckpointBytes = 1024,
        });
        var created = service.Create(
            "/bin/sh", "[\"-c\",\"printf '%0512d' 0; sleep 30\"]",
            retentionMode: TerminalRetentionMode.Reattachable);
        var attached = service.Attach(created.SessionId, created.AccessToken, 34, "0", 1024);
        var read = await service.ReadAsync(
            created.SessionId, attached.AttachmentId, attached.Generation, 34, 1024, 1000);
        Convert.FromBase64String(read.DataBase64).Should().HaveCount(512);
        service.Ack(created.SessionId, attached.AttachmentId, attached.Generation, 34, read.EndOffset)
            .Ok.Should().BeTrue();

        var checkpoint = service.StoreCheckpoint(
            created.SessionId, attached.AttachmentId, attached.Generation, 34, read.EndOffset,
            "checkpoint-v1", Convert.ToBase64String(new byte[700]), 80, 24);

        checkpoint.Ok.Should().BeTrue();
        service.Status(created.SessionId, attached.AttachmentId, attached.Generation, 34)
            .RetainedBytes.Should().Be(700);
    }

    [Fact]
    public void Terminal_CheckpointReplacement_IsAtomicAgainstGlobalBudget()
    {
        if (OperatingSystem.IsWindows()) return;
        using var service = TerminalService(new TerminalOptions
        {
            AttachmentCreditBytes = 64,
            SessionRetentionBytes = 1024,
            GlobalRetentionBytes = 1024,
            MaxReadBytes = 64,
            MaxCheckpointBytes = 1024,
        });
        var firstCreated = service.Create(
            "/bin/sh", "[\"-c\",\"sleep 30\"]", retentionMode: TerminalRetentionMode.Reattachable);
        var secondCreated = service.Create(
            "/bin/sh", "[\"-c\",\"sleep 30\"]", retentionMode: TerminalRetentionMode.Reattachable);
        var first = service.Attach(firstCreated.SessionId, firstCreated.AccessToken, 35, "0");
        var second = service.Attach(secondCreated.SessionId, secondCreated.AccessToken, 36, "0");

        Store(firstCreated, first, 35, 700).Ok.Should().BeTrue();
        Store(secondCreated, second, 36, 300).Ok.Should().BeTrue();
        Store(firstCreated, first, 35, 800).Error.Should().Be("budget_exceeded");
        service.Attach(firstCreated.SessionId, firstCreated.AccessToken, 35, "0", mode: TerminalAttachmentMode.Mirror)
            .Checkpoint!.PayloadBase64.Should().Be(Convert.ToBase64String(new byte[700]));
        Store(secondCreated, second, 36, 400).Error.Should().Be("budget_exceeded");
        Store(firstCreated, first, 35, 200).Ok.Should().BeTrue();
        Store(secondCreated, second, 36, 400).Ok.Should().BeTrue();

        TerminalOperationResult Store(
            TerminalCreateResult created, TerminalAttachResult attached, long windowId, int bytes) =>
            service.StoreCheckpoint(
                created.SessionId, attached.AttachmentId, attached.Generation, windowId, "0",
                "checkpoint-v1", Convert.ToBase64String(new byte[bytes]), 80, 24);
    }

    [Fact]
    public async Task Terminal_SlowMirror_DoesNotBlockControllerAndRequiresResync()
    {
        if (OperatingSystem.IsWindows()) return;
        using var service = TerminalService(new TerminalOptions
        {
            AttachmentCreditBytes = 64,
            SessionRetentionBytes = 256,
            GlobalRetentionBytes = 1024,
            MaxReadBytes = 64,
        });
        var created = service.Create("/bin/sh", "[\"-c\",\"printf '%01024d' 0\"]");
        var controller = service.Attach(created.SessionId, created.AccessToken, 37, "0", 64);
        var mirror = service.Attach(
            created.SessionId, created.AccessToken, 38, "0", 64, TerminalAttachmentMode.Mirror);
        var bytes = new List<byte>();
        var offset = "0";
        while (true)
        {
            var read = await service.ReadAsync(
                created.SessionId, controller.AttachmentId, controller.Generation, 37, 64, 1000);
            bytes.AddRange(Convert.FromBase64String(read.DataBase64));
            offset = read.EndOffset;
            service.Ack(created.SessionId, controller.AttachmentId, controller.Generation, 37, offset)
                .Ok.Should().BeTrue();
            if (read.ProcessExited && read.FinalOffset == offset) break;
        }

        bytes.Should().HaveCount(1024);
        var status = service.Status(created.SessionId, controller.AttachmentId, controller.Generation, 37);
        status.RetainedBytes.Should().BeLessThanOrEqualTo(256);
        var slow = await service.ReadAsync(
            created.SessionId, mirror.AttachmentId, mirror.Generation, 38, 64, 0);
        slow.Error.Should().Be("resync_required");
    }

    [Fact]
    public async Task Terminal_DetachedReattachableSession_PausesAtValidBaselineWithoutEviction()
    {
        if (OperatingSystem.IsWindows()) return;
        using var service = TerminalService(new TerminalOptions
        {
            AttachmentCreditBytes = 64,
            SessionRetentionBytes = 256,
            GlobalRetentionBytes = 1024,
            MaxReadBytes = 64,
        });
        var created = service.Create(
            "/bin/sh", "[\"-c\",\"printf '%01024d' 0; sleep 30\"]",
            retentionMode: TerminalRetentionMode.Reattachable);
        var first = service.Attach(created.SessionId, created.AccessToken, 39, "0", 64);
        service.Detach(created.SessionId, first.AttachmentId, first.Generation, 39).Ok.Should().BeTrue();
        await Task.Delay(200);

        var attached = service.Attach(created.SessionId, created.AccessToken, 39, "0", 64);
        attached.RetainedStart.Should().Be("0");
        var read = await service.ReadAsync(
            created.SessionId, attached.AttachmentId, attached.Generation, 39, 64, 1000);
        read.StartOffset.Should().Be("0");
        Convert.FromBase64String(read.DataBase64).Should().HaveCount(64);
        service.Status(created.SessionId, attached.AttachmentId, attached.Generation, 39)
            .RetainedBytes.Should().BeLessThanOrEqualTo(256);
    }

    [Fact]
    public void Terminal_ResizeLease_RejectsStaleAndHiddenViewer()
    {
        if (OperatingSystem.IsWindows()) return;
        using var service = TerminalService();
        var created = service.Create("/bin/sh", "[\"-c\",\"sleep 5\"]");
        var owner = service.Attach(created.SessionId, created.AccessToken, 41, "0");
        var mirror = service.Attach(created.SessionId, created.AccessToken, 42, "0", mode: TerminalAttachmentMode.Mirror);
        service.ClaimResize(created.SessionId, mirror.AttachmentId, mirror.Generation, 42).Error
            .Should().Be("unauthorized_attachment");
        var lease = service.ClaimResize(created.SessionId, owner.AttachmentId, owner.Generation, 41);
        lease.Ok.Should().BeTrue();
        service.Resize(created.SessionId, owner.AttachmentId, owner.Generation, 41,
            lease.LeaseId!, lease.LeaseGeneration!, 100, 40).Ok.Should().BeTrue();
        service.ReleaseResize(created.SessionId, owner.AttachmentId, owner.Generation, 41,
            lease.LeaseId!, lease.LeaseGeneration!).Ok.Should().BeTrue();
        service.Resize(created.SessionId, owner.AttachmentId, owner.Generation, 41,
            lease.LeaseId!, lease.LeaseGeneration!, 90, 30).Error.Should().Be("stale_generation");
        service.Terminate(created.SessionId, owner.AttachmentId, owner.Generation, 41);
    }

    [Fact]
    public async Task Terminal_Resize_ChangesDimensionsObservedByChild()
    {
        if (OperatingSystem.IsWindows()) return;
        using var service = TerminalService();
        var created = service.Create("/bin/sh", "[\"-c\",\"read line; stty size\"]");
        var owner = service.Attach(created.SessionId, created.AccessToken, 41, "0");
        var lease = service.ClaimResize(created.SessionId, owner.AttachmentId, owner.Generation, 41);
        service.Resize(created.SessionId, owner.AttachmentId, owner.Generation, 41,
            lease.LeaseId!, lease.LeaseGeneration!, 101, 37).Ok.Should().BeTrue();
        await service.WriteAsync(created.SessionId, owner.AttachmentId, owner.Generation, 41,
            Convert.ToBase64String("size\n"u8));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var output = new System.Text.StringBuilder();
        while (true)
        {
            var packet = await service.ReadAsync(created.SessionId, owner.AttachmentId, owner.Generation, 41,
                cancellationToken: timeout.Token);
            output.Append(System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(packet.DataBase64)));
            service.Ack(created.SessionId, owner.AttachmentId, owner.Generation, 41, packet.EndOffset);
            if (packet.ProcessExited && packet.FinalOffset == packet.EndOffset) break;
        }
        output.ToString().Should().MatchRegex(@"37\s+101");
    }


    [Fact]
    public async Task Terminal_Dispose_CompletesPendingReadAndKillsChildWithoutAck()
    {
        if (OperatingSystem.IsWindows()) return;
        var service = TerminalService();
        var created = service.Create("/bin/sh", "[\"-c\",\"sleep 30\"]");
        var attached = service.Attach(created.SessionId, created.AccessToken, 51, "0");
        var pending = service.ReadAsync(
            created.SessionId, attached.AttachmentId, attached.Generation, 51, 64, 20_000).AsTask();

        service.Dispose();

        var read = await pending.WaitAsync(TimeSpan.FromSeconds(2));
        read.Error.Should().Be("cancelled");
    }

    [Fact]
    public async Task Terminal_ExitIsObservableWithoutAckAtCapacity()
    {
        if (OperatingSystem.IsWindows()) return;
        using var service = TerminalService(new TerminalOptions
        {
            AttachmentCreditBytes = 64,
            SessionRetentionBytes = 4096,
            GlobalRetentionBytes = 4096,
            MaxReadBytes = 64,
            ExitDrainTimeout = TimeSpan.FromMilliseconds(200),
        });
        var created = service.Create("/bin/sh", "[\"-c\",\"printf '%0128d' 0\"]");
        var attached = service.Attach(created.SessionId, created.AccessToken, 62, "0", 64);
        TerminalReadResult first;
        var firstReadDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        do
        {
            first = await service.ReadAsync(
                created.SessionId, attached.AttachmentId, attached.Generation, 62, 64, 1000);
            first.Error.Should().BeNull();
            if (first.DataBase64.Length != 0 || first.FinalOffset is not null) break;
            // Child exit can wake a read before the independent native drain delivers bytes.
            await Task.Delay(10);
        } while (DateTime.UtcNow < firstReadDeadline);
        Convert.FromBase64String(first.DataBase64).Should().HaveCount(64);

        TerminalStatusResult status;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        do
        {
            status = service.Status(created.SessionId, attached.AttachmentId, attached.Generation, 62);
            if (status.ProcessExited) break;
            await Task.Delay(20);
        } while (DateTime.UtcNow < deadline);

        status.ProcessExited.Should().BeTrue();
        status.FinalOffset.Should().Be("128");
        status.RetainedBytes.Should().BeLessThanOrEqualTo(4096);
        status.OutputTruncated.Should().BeFalse();
        service.Ack(created.SessionId, attached.AttachmentId, attached.Generation, 62, first.EndOffset)
            .Ok.Should().BeTrue();
        var tail = await service.ReadAsync(
            created.SessionId, attached.AttachmentId, attached.Generation, 62, 64, 1000);
        Convert.FromBase64String(tail.DataBase64).Should().HaveCount(64);
        tail.FinalOffset.Should().Be(tail.EndOffset);
    }

    [Fact]
    public async Task Terminal_ExitDrainDeadline_HandlesDescendantHoldingSlave()
    {
        if (OperatingSystem.IsWindows()) return;
        using var service = TerminalService(new TerminalOptions
        {
            AttachmentCreditBytes = 64,
            SessionRetentionBytes = 64,
            GlobalRetentionBytes = 64,
            MaxReadBytes = 64,
            ExitDrainTimeout = TimeSpan.FromMilliseconds(100),
        });
        var created = service.Create("/bin/sh", "[\"-c\",\"sleep 30 & printf done\"]");
        var attached = service.Attach(created.SessionId, created.AccessToken, 63, "0", 64);

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        TerminalStatusResult status;
        do
        {
            status = service.Status(created.SessionId, attached.AttachmentId, attached.Generation, 63);
            if (status.ProcessExited) break;
            await Task.Delay(20);
        } while (DateTime.UtcNow < deadline);

        status.ProcessExited.Should().BeTrue();
        await Task.Delay(200);
        var final = service.Status(created.SessionId, attached.AttachmentId, attached.Generation, 63);
        final.FinalOffset.Should().NotBeNull();
    }


    [Fact]
    public async Task Terminal_Detach_DuringPendingRead_DoesNotFaultCleanup()
    {
        if (OperatingSystem.IsWindows()) return;
        using var service = TerminalService();
        var created = service.Create("/bin/sh", "[\"-c\",\"sleep 30\"]");
        var attached = service.Attach(created.SessionId, created.AccessToken, 53, "0");
        var pending = service.ReadAsync(
            created.SessionId, attached.AttachmentId, attached.Generation, 53, 64, 20_000).AsTask();

        service.Detach(created.SessionId, attached.AttachmentId, attached.Generation, 53)
            .Ok.Should().BeTrue();

        var read = await pending.WaitAsync(TimeSpan.FromSeconds(2));
        read.Error.Should().Be("unauthorized_attachment");
    }

    [UnixFact]
    public async Task Terminal_Dispose_CancelsObservedBlockedNativeWriter()
    {
        using var service = TerminalService();
        var native = service.SpawnNative("/bin/sh",
            "[\"-c\",\"stty raw -echo; printf READY; exec sleep 30\"]", 80, 24);
        Task<bool>? writing = null;
        try
        {
            var ready = new byte[64];
            var count = native.Read(ready, ready.Length, 2000);
            count.Should().BeGreaterThan(0);
            System.Text.Encoding.UTF8.GetString(ready, 0, count).Should().Contain("READY");
            writing = Task.Run(() => native.Write(new byte[8 * 1024 * 1024]));
            await Task.Delay(200);
            writing.IsCompleted.Should().BeFalse("raw stdin is saturated and the child never reads");

            await Task.Run(service.Dispose).WaitAsync(TimeSpan.FromSeconds(2));
            (await writing.WaitAsync(TimeSpan.FromSeconds(5))).Should().BeFalse();
        }
        finally
        {
            native.RequestKill();
            if (writing is not null) await writing.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Run(() => native.WaitForExit()).WaitAsync(TimeSpan.FromSeconds(5));
            native.CompleteLifetime();
        }
    }

    private static TerminalSessionService TerminalService(TerminalOptions? options = null) =>
        new(Policy(new ShellOptions { AllowedCommands = ["/bin/sh"] }), options ?? new TerminalOptions());
    [Fact]
    public void FullPathAllowlist_DoesNotPermitBareInvocation()
    {
        using var fixture = new CommandFixture("mytool");

        var policy = Policy(new ShellOptions { AllowedCommands = [fixture.FullPath] });

        var act = () => policy.ValidateAndResolveCommand("mytool");
        act.Should().Throw<UnauthorizedAccessException>();
    }

    [Fact]
    public void BareAllowlist_ResolvesToConfigTimeCanonicalPath()
    {
        using var fixture = new CommandFixture("mytool");

        var originalPath = Environment.GetEnvironmentVariable("PATH");
        Environment.SetEnvironmentVariable("PATH", fixture.Directory + (OperatingSystem.IsWindows() ? ";" : ":") + originalPath);
        try
        {
            var policy = Policy(new ShellOptions { AllowedCommands = ["mytool"] });

            var resolved = policy.ValidateAndResolveCommand("mytool");

            resolved.Should().NotBe("mytool");
            Path.IsPathRooted(resolved).Should().BeTrue();
            resolved.Should().Be(fixture.FullPath);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", originalPath);
        }
    }

    [Fact]
    public void UnresolvedBareAllowlist_RejectsAtInvocation()
    {
        var policy = Policy(new ShellOptions
        {
            AllowedCommands = ["this_command_does_not_exist_anywhere_in_path"]
        });

        var act = () => policy.ValidateAndResolveCommand("this_command_does_not_exist_anywhere_in_path");
        act.Should().Throw<UnauthorizedAccessException>();
    }

    [Fact]
    public void FullPathAllowlist_PermitsExactPathInvocation()
    {
        using var fixture = new CommandFixture("mytool");

        var policy = Policy(new ShellOptions { AllowedCommands = [fixture.FullPath] });

        var resolved = policy.ValidateAndResolveCommand(fixture.FullPath);
        resolved.Should().Be(fixture.FullPath);
    }

    [Fact]
    public void BareAllowlist_CannotBeHijackedByLaterPathEntry()
    {
        using var legitimate = new CommandFixture("mytool");
        using var malicious = new CommandFixture("mytool");

        var originalPath = Environment.GetEnvironmentVariable("PATH");
        var sep = OperatingSystem.IsWindows() ? ";" : ":";
        try
        {
            // Configure (i.e. construct the policy) with legitimate first in PATH
            Environment.SetEnvironmentVariable("PATH",
                legitimate.Directory + sep + originalPath);
            var policy = Policy(new ShellOptions { AllowedCommands = ["mytool"] });

            // Now swap PATH so malicious comes first
            Environment.SetEnvironmentVariable("PATH",
                malicious.Directory + sep + originalPath);

            // Resolution is pinned to construction time — malicious binary is ignored
            var resolved = policy.ValidateAndResolveCommand("mytool");
            resolved.Should().Be(legitimate.FullPath);
            resolved.Should().NotBe(malicious.FullPath);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", originalPath);
        }
    }

    private sealed class CommandFixture : IDisposable
    {
        public string Directory { get; }
        public string FullPath { get; }

        public CommandFixture(string name)
        {
            Directory = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            System.IO.Directory.CreateDirectory(Directory);

            var ext = OperatingSystem.IsWindows() ? ".exe" : "";
            FullPath = Path.GetFullPath(Path.Combine(Directory, name + ext));
            File.WriteAllBytes(FullPath, [0]);

            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(FullPath, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        }

        public void Dispose()
        {
            try { System.IO.Directory.Delete(Directory, recursive: true); }
            catch (IOException) { /* cleanup best-effort */ }
        }
    }
}
