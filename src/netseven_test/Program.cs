
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.RateLimiting;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;
using RedisRateLimiting;
using StackExchange.Redis;

namespace netseven_test
{
    public class CustomRateLimiterPolicy : IRateLimiterPolicy<string>
    {
        private readonly ILogger<CustomRateLimiterPolicy> _logger;
        private readonly IConnectionMultiplexer _connectionMultiplexer;

        public CustomRateLimiterPolicy(
            ILogger<CustomRateLimiterPolicy> logger,
            IConnectionMultiplexer connectionMultiplexer)
        {
            _logger = logger;
            _connectionMultiplexer = connectionMultiplexer;
        }

        public Func<OnRejectedContext, CancellationToken, ValueTask>? OnRejected
        {
            get => (context, cancellationToken) =>
            {
                var meta = context.Lease.GetAllMetadata();
                foreach (var p in meta)
                    _logger.LogInformation($"{p.Key}={p.Value}");

                var success = context.Lease.TryGetMetadata("RETRY_AFTER", out var retryAfter);
                if (!success) retryAfter = new TimeSpan(0, 0, 10);

                context.HttpContext.Response.Headers.RetryAfter = new Microsoft.Extensions.Primitives.StringValues(retryAfter?.ToString());
                return System.Threading.Tasks.ValueTask.CompletedTask;
            };
        }

        public RateLimitPartition<string> GetPartition(HttpContext context)
        {
            var authHeader = context.Request.Headers.Authorization.ToString();
            var sourceIp = context.Request.Host.ToString();

            var values = new List<string>();
            if (!string.IsNullOrWhiteSpace(authHeader)) values.Add(authHeader);
            if (!string.IsNullOrWhiteSpace(sourceIp)) values.Add(sourceIp);

            var partition = string.Join("-", values);
            _logger.LogInformation($"Partition => {partition}");

            if (!StringValues.IsNullOrEmpty(partition))
            {
                return RedisRateLimitPartition.GetRedisTokenBucketRateLimiter("auth_header", key =>
                    new RedisTokenBucketRateLimiterOptions()
                    {
                        TokenLimit = 5,
                        ConnectionMultiplexerFactory = () => _connectionMultiplexer,
                        ReplenishmentPeriod = TimeSpan.FromSeconds(5),
                        TokensPerPeriod = 1                        
                    });
            }
            else
            {
                return RedisRateLimitPartition.GetRedisFixedWindowRateLimiter("default", key => new RedisFixedWindowRateLimiterOptions
                {
                    ConnectionMultiplexerFactory = () => _connectionMultiplexer,
                    PermitLimit = 10,
                    Window = TimeSpan.FromSeconds(10)
            });
            }
        }
    }


    public class Program
    {
        public static void Main(string[] args)
        {
            var builder = WebApplication.CreateBuilder(args);

            // Add services to the container.

            var connectionMultiplexer = ConnectionMultiplexer.Connect(Environment.GetEnvironmentVariable("REDIS_CONNECTION"));

            builder.Services.AddControllers();
            // Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
            builder.Services.AddEndpointsApiExplorer();
            builder.Services.AddSwaggerGen();
            builder.Services.AddSingleton<IConnectionMultiplexer>(connectionMultiplexer);
            builder.Services.AddTransient<CustomRateLimiterPolicy>();
            builder.Services.AddRateLimiter(options =>
            {
                options.AddPolicy();
            });

            var app = builder.Build();
            app.UseRateLimiter();

            // Configure the HTTP request pipeline.
            if (app.Environment.IsDevelopment())
            {
                app.UseSwagger();
                app.UseSwaggerUI();
            }

            app.UseHttpsRedirection();

            app.UseAuthorization();


            app.MapControllers();

            app.Run();
        }
    }
}