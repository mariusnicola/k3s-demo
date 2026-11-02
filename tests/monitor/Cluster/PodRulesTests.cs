using k8s.Models;
using Xunit;

public class PodRulesTests
{
    private static V1Pod Pod(string phase, bool?[]? ready = null, bool deleting = false) => new()
    {
        Metadata = new V1ObjectMeta { Name = "p", DeletionTimestamp = deleting ? DateTime.UtcNow : null },
        Status = new V1PodStatus
        {
            Phase = phase,
            ContainerStatuses = ready?.Select(r => new V1ContainerStatus { Ready = r ?? false, Name = "c", Image = "i", ImageID = "i", RestartCount = 0 }).ToList()
        }
    };

    private static readonly DateTimeOffset Now = new(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);

    private static V1CronJob Cron(bool suspended = false, DateTimeOffset? last = null) => new()
    {
        Metadata = new V1ObjectMeta { Name = "nightly" },
        Spec = new V1CronJobSpec { Suspend = suspended, Schedule = "0 2 * * *" },
        Status = new V1CronJobStatus { LastScheduleTime = last?.UtcDateTime }
    };

    private static V1Job Job(string name, DateTimeOffset started, string? condition, string? reason = null) => new()
    {
        Metadata = new V1ObjectMeta { Name = name, OwnerReferences = [new V1OwnerReference { Name = "nightly", Kind = "CronJob", ApiVersion = "batch/v1", Uid = "u" }] },
        Status = new V1JobStatus
        {
            StartTime = started.UtcDateTime,
            Conditions = condition is null ? null : [new V1JobCondition { Type = condition, Status = "True", Reason = reason }]
        }
    };

    [Theory]
    [InlineData("Running", true)]
    [InlineData("Pending", true)]
    [InlineData("Failed", true)]
    [InlineData("Unknown", true)]
    [InlineData("Succeeded", false)]
    public void Every_pod_shows_but_a_finished_one(string phase, bool shown)
    {
        Assert.Equal(shown, PodRules.Shown(Pod(phase)));
    }

    [Fact]
    public void A_pod_being_deleted_shows_as_terminating_because_it_may_still_serve()
    {
        Assert.True(PodRules.Shown(Pod("Running", deleting: true)));
        Assert.True(PodRules.Terminating(Pod("Running", deleting: true)));
        Assert.False(PodRules.Terminating(Pod("Running")));
    }

    [Fact]
    public void A_finished_pod_being_deleted_stays_out()
    {
        Assert.False(PodRules.Shown(Pod("Succeeded", deleting: true)));
    }

    [Fact]
    public void A_pod_being_deleted_counts_against_no_cap()
    {
        Assert.False(PodRules.HoldsResources(Pod("Running", deleting: true)));
    }

    [Theory]
    [InlineData("Running", true)]
    [InlineData("Pending", true)]
    [InlineData("Failed", false)]
    public void Only_a_pod_that_holds_resources_counts_against_caps(string phase, bool counted)
    {
        Assert.Equal(counted, PodRules.HoldsResources(Pod(phase)));
    }

    [Fact]
    public void A_pod_is_ready_only_when_every_container_is()
    {
        Assert.True(PodRules.Ready(Pod("Running", [true, true])));
        Assert.False(PodRules.Ready(Pod("Running", [true, false])));
        Assert.False(PodRules.Ready(Pod("Pending")));
    }

    private static V1Lease Lease(string? holder, DateTimeOffset? renewed, int? seconds = 10) => new()
    {
        Spec = new V1LeaseSpec { HolderIdentity = holder, RenewTime = renewed?.UtcDateTime, LeaseDurationSeconds = seconds }
    };

    [Fact]
    public void A_lease_renewed_within_its_time_names_the_leader()
    {
        Assert.Equal("dispatcher-a", LeaseRule.Leader(Lease("dispatcher-a", Now.AddSeconds(-9)), Now));
    }

    [Fact]
    public void A_lease_past_its_time_names_nobody()
    {
        Assert.Null(LeaseRule.Leader(Lease("dispatcher-a", Now.AddSeconds(-11)), Now));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void A_released_lease_names_nobody(string? holder)
    {
        Assert.Null(LeaseRule.Leader(Lease(holder, Now), Now));
    }

    [Fact]
    public void A_lease_with_no_renewal_names_nobody()
    {
        Assert.Null(LeaseRule.Leader(Lease("dispatcher-a", null), Now));
    }

    [Fact]
    public void A_cronjob_that_ran_well_has_no_problem()
    {
        var cron = Cron(last: Now.AddHours(-10));

        Assert.Null(NightlyRule.Problem(cron, [Job("n1", Now.AddHours(-10), "Complete")], Now));
    }

    [Fact]
    public void A_cronjob_that_never_ran_yet_has_no_problem()
    {
        Assert.Null(NightlyRule.Problem(Cron(), [], Now));
    }

    [Fact]
    public void A_suspended_cronjob_is_a_problem()
    {
        Assert.Contains("suspended", NightlyRule.Problem(Cron(suspended: true), [], Now));
    }

    [Fact]
    public void A_failed_last_run_is_a_problem_and_names_the_reason()
    {
        var problem = NightlyRule.Problem(Cron(last: Now.AddHours(-1)), [Job("n1", Now.AddHours(-1), "Failed", "BackoffLimitExceeded")], Now);

        Assert.Contains("n1", problem);
        Assert.Contains("BackoffLimitExceeded", problem);
    }

    [Fact]
    public void Only_the_newest_run_decides()
    {
        var cron = Cron(last: Now.AddHours(-1));
        var jobs = new[] { Job("old", Now.AddHours(-25), "Failed", "x"), Job("new", Now.AddHours(-1), "Complete") };

        Assert.Null(NightlyRule.Problem(cron, jobs, Now));
    }

    [Fact]
    public void A_job_of_another_owner_is_ignored()
    {
        var other = Job("manual", Now, "Failed", "x");
        other.Metadata.OwnerReferences = null;

        Assert.Null(NightlyRule.Problem(Cron(last: Now.AddHours(-1)), [other], Now));
    }

    [Fact]
    public void A_schedule_that_stopped_firing_is_a_problem()
    {
        var problem = NightlyRule.Problem(Cron(last: Now.AddHours(-26)), [Job("n1", Now.AddHours(-26), "Complete")], Now);

        Assert.Contains("no run", problem);
    }
}
