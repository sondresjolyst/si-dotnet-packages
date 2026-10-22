using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace SjolystInnovation.AppKit.Email.Tests;

public class EmailReceiptGateTests
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 14, 12, 0, 0, TimeSpan.Zero));

    private EmailReceiptGate Gate(Action<EmailReceiptGateOptions>? configure = null)
    {
        var options = new EmailReceiptGateOptions();
        configure?.Invoke(options);
        return new EmailReceiptGate(Options.Create(options), _time);
    }

    [Fact]
    public void A_mailbox_gets_one_receipt_per_window()
    {
        var gate = Gate();
        Assert.True(gate.TryAcquire("kari@example.no"));
        Assert.False(gate.TryAcquire("kari@example.no"));
        _time.Advance(TimeSpan.FromMinutes(9));
        Assert.False(gate.TryAcquire("kari@example.no"));
        _time.Advance(TimeSpan.FromMinutes(1));
        Assert.True(gate.TryAcquire("kari@example.no"));
    }

    [Theory]
    [InlineData("kari@example.no", "KARI@Example.NO")]
    [InlineData("kari@example.no", "kari+x@example.no")]
    [InlineData("kari@example.no", "  kari@example.no ")]
    [InlineData("kari.n@gmail.com", "karin@googlemail.com")]
    public void Spellings_of_one_mailbox_count_as_one(string first, string second)
    {
        var gate = Gate();
        Assert.True(gate.TryAcquire(first));
        Assert.False(gate.TryAcquire(second));
    }

    [Fact]
    public void Dots_only_fold_for_gmail()
    {
        var gate = Gate();
        Assert.True(gate.TryAcquire("ka.ri@example.no"));
        Assert.True(gate.TryAcquire("kari@example.no"));
    }

    [Fact]
    public void The_overall_cap_holds_across_mailboxes_until_the_window_passes()
    {
        var gate = Gate(o => o.GlobalLimit = 3);
        Assert.True(gate.TryAcquire("a@example.no"));
        Assert.True(gate.TryAcquire("b@example.no"));
        Assert.True(gate.TryAcquire("c@example.no"));
        Assert.False(gate.TryAcquire("d@example.no"));
        _time.Advance(TimeSpan.FromMinutes(59));
        Assert.False(gate.TryAcquire("d@example.no"));
        _time.Advance(TimeSpan.FromMinutes(1));
        Assert.True(gate.TryAcquire("d@example.no"));
    }

    [Fact]
    public void A_refused_receipt_is_not_counted()
    {
        var gate = Gate(o => o.GlobalLimit = 1);
        Assert.True(gate.TryAcquire("a@example.no"));
        Assert.False(gate.TryAcquire("a@example.no"));
        Assert.False(gate.TryAcquire("b@example.no"));
        _time.Advance(TimeSpan.FromHours(1));
        Assert.True(gate.TryAcquire("b@example.no"));
    }

    [Fact]
    public void The_windows_follow_the_options()
    {
        var gate = Gate(o =>
        {
            o.AddressWindow = TimeSpan.FromSeconds(30);
            o.GlobalWindow = TimeSpan.FromSeconds(45);
            o.GlobalLimit = 2;
        });
        Assert.True(gate.TryAcquire("a@example.no"));
        _time.Advance(TimeSpan.FromSeconds(30));
        Assert.True(gate.TryAcquire("a@example.no"));
        Assert.False(gate.TryAcquire("b@example.no"));
        _time.Advance(TimeSpan.FromSeconds(15));
        Assert.True(gate.TryAcquire("b@example.no"));
    }

    [Fact]
    public void The_defaults_are_ten_minutes_one_hour_and_thirty()
    {
        var options = new EmailReceiptGateOptions();
        Assert.Equal(TimeSpan.FromMinutes(10), options.AddressWindow);
        Assert.Equal(TimeSpan.FromHours(1), options.GlobalWindow);
        Assert.Equal(30, options.GlobalLimit);
    }

    [Fact]
    public void Options_that_are_not_above_zero_are_refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Gate(o => o.AddressWindow = TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => Gate(o => o.GlobalWindow = TimeSpan.FromSeconds(-1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => Gate(o => o.GlobalLimit = 0));
    }

    [Fact]
    public void Registration_gives_one_shared_gate_with_the_configured_limits()
    {
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(_time);
        services.AddAppKitEmailReceiptGate(o => o.GlobalLimit = 1);
        using var provider = services.BuildServiceProvider();

        var gate = provider.GetRequiredService<EmailReceiptGate>();
        Assert.Same(gate, provider.GetRequiredService<EmailReceiptGate>());
        Assert.True(gate.TryAcquire("a@example.no"));
        Assert.False(gate.TryAcquire("b@example.no"));
    }

    [Fact]
    public void Registration_falls_back_to_the_system_clock_and_the_defaults()
    {
        var services = new ServiceCollection();
        services.AddAppKitEmailReceiptGate();
        using var provider = services.BuildServiceProvider();

        Assert.Same(TimeProvider.System, provider.GetRequiredService<TimeProvider>());
        Assert.Equal(30, provider.GetRequiredService<IOptions<EmailReceiptGateOptions>>().Value.GlobalLimit);
        Assert.True(provider.GetRequiredService<EmailReceiptGate>().TryAcquire("a@example.no"));
    }

    [Fact]
    public void Registration_binds_a_configuration_section()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Receipts:AddressWindow"] = "00:02:00",
            ["Receipts:GlobalLimit"] = "5",
        }).Build();
        var services = new ServiceCollection();
        services.AddAppKitEmailReceiptGate(configuration.GetSection("Receipts"));
        using var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<EmailReceiptGateOptions>>().Value;
        Assert.Equal(TimeSpan.FromMinutes(2), options.AddressWindow);
        Assert.Equal(TimeSpan.FromHours(1), options.GlobalWindow);
        Assert.Equal(5, options.GlobalLimit);
    }
}
