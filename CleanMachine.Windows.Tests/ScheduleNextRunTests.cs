using CleanMachine.Windows;
using Xunit;

namespace CleanMachine.Windows.Tests;

/// <summary>Pins the "Next run" time shown after a schedule is saved, so the
/// confirmation never names a time Task Scheduler would not actually use.</summary>
public sealed class ScheduleNextRunTests
{
    // Wednesday 15 October 2025, 10:30.
    private static readonly DateTime Now = new(2025, 10, 15, 10, 30, 0);

    [Fact]
    public void DailyRunsLaterTodayOrTomorrow()
    {
        Assert.Equal(new DateTime(2025, 10, 15, 22, 0, 0),
            ScheduledTask.NextRun(new CleanupSchedule { Trigger = ScheduleTrigger.Daily, Hour = 22 }, Now));
        Assert.Equal(new DateTime(2025, 10, 16, 3, 0, 0),
            ScheduledTask.NextRun(new CleanupSchedule { Trigger = ScheduleTrigger.Daily, Hour = 3 }, Now));
        // Exactly now has already fired.
        Assert.Equal(new DateTime(2025, 10, 16, 10, 30, 0),
            ScheduledTask.NextRun(new CleanupSchedule { Trigger = ScheduleTrigger.Daily, Hour = 10, Minute = 30 }, Now));
    }

    [Fact]
    public void WeeklyRunsOnTheNextMatchingDay()
    {
        Assert.Equal(new DateTime(2025, 10, 19, 3, 0, 0),
            ScheduledTask.NextRun(new CleanupSchedule { Trigger = ScheduleTrigger.Weekly, DayOfWeek = DayOfWeek.Sunday, Hour = 3 }, Now));
        // Today, but the time has passed: a week from today.
        Assert.Equal(new DateTime(2025, 10, 22, 9, 0, 0),
            ScheduledTask.NextRun(new CleanupSchedule { Trigger = ScheduleTrigger.Weekly, DayOfWeek = DayOfWeek.Wednesday, Hour = 9 }, Now));
        // Today, still ahead.
        Assert.Equal(new DateTime(2025, 10, 15, 18, 0, 0),
            ScheduledTask.NextRun(new CleanupSchedule { Trigger = ScheduleTrigger.Weekly, DayOfWeek = DayOfWeek.Wednesday, Hour = 18 }, Now));
    }

    [Fact]
    public void MonthlySkipsMonthsWithoutTheDay()
    {
        Assert.Equal(new DateTime(2025, 11, 1, 3, 0, 0),
            ScheduledTask.NextRun(new CleanupSchedule { Trigger = ScheduleTrigger.Monthly, DayOfMonth = 1, Hour = 3 }, Now));
        Assert.Equal(new DateTime(2025, 10, 31, 3, 0, 0),
            ScheduledTask.NextRun(new CleanupSchedule { Trigger = ScheduleTrigger.Monthly, DayOfMonth = 31, Hour = 3 }, Now));
        // From 31 Oct after the run: November has no 31st, so December.
        Assert.Equal(new DateTime(2025, 12, 31, 3, 0, 0),
            ScheduledTask.NextRun(new CleanupSchedule { Trigger = ScheduleTrigger.Monthly, DayOfMonth = 31, Hour = 3 },
                new DateTime(2025, 10, 31, 4, 0, 0)));
        // The 29th of February only in a leap year.
        Assert.Equal(new DateTime(2028, 2, 29, 3, 0, 0),
            ScheduledTask.NextRun(new CleanupSchedule { Trigger = ScheduleTrigger.Monthly, DayOfMonth = 29, Hour = 3 },
                new DateTime(2028, 2, 1)));
    }

    [Fact]
    public void AtLogonHasNoTimeAndSaysSo()
    {
        var schedule = new CleanupSchedule { Trigger = ScheduleTrigger.AtLogon };

        Assert.Null(ScheduledTask.NextRun(schedule, Now));
        Assert.Equal("It will run the next time you sign in to Windows.", ScheduledTask.DescribeNextRun(schedule, Now));
    }

    [Fact]
    public void TimedSchedulesNameTheNextRun()
        => Assert.StartsWith("Next run: ",
            ScheduledTask.DescribeNextRun(new CleanupSchedule { Trigger = ScheduleTrigger.Daily, Hour = 22 }, Now));
}
