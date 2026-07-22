using System.Runtime.InteropServices;
using FluentAssertions;
using Ryn.Core.Internal;
using Xunit;

namespace Ryn.Core.Tests.Internal;

public sealed class NativeThreadTests
{
    [Theory]
    [InlineData(42, 42, true)]
    [InlineData(42, 43, false)]
    public void IsLinuxInitialThread_ComparesProcessAndThreadIds(int processId, int threadId, bool expected) =>
        NativeThread.IsLinuxInitialThread(processId, threadId).Should().Be(expected);

    [Theory]
    [InlineData(Architecture.X64, 186)]
    [InlineData(Architecture.Arm64, 178)]
    public void GetLinuxGetThreadIdSyscallNumber_ReturnsArchitectureValue(
        Architecture architecture,
        int expected) =>
        NativeThread.GetLinuxGetThreadIdSyscallNumber(architecture).Should().Be(expected);

    [Fact]
    public void IsInitialThread_RejectsWorkerThreadOnUnix()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) return;

        bool? isInitialThread = null;
        Exception? exception = null;
        var worker = new Thread(() =>
            exception = Record.Exception(() => isInitialThread = NativeThread.IsInitialThread()));

        worker.Start();
        worker.Join();

        exception.Should().BeNull();
        isInitialThread.Should().BeFalse();
    }
}
