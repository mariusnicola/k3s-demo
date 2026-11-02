using BatchTracker.Api;
using BatchTracker.Application;
using BatchTracker.Dispatcher;
using BatchTracker.Infrastructure;
using BatchTracker.Line;
using BatchTracker.Live;
using BatchTracker.Worker;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace BatchTracker.ArchTests;

public sealed record Route(string Method, string Path, bool Anonymous, IReadOnlyList<string?> Policies, bool RateExempt = false, bool HealthBucket = false, bool MachineBucket = false);
