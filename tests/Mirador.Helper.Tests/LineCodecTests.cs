using System.Text;
using Mirador.Ipc;

namespace Mirador.Helper.Tests;

public class LineCodecTests
{
    private static MemoryStream Bytes(string text) => new(Encoding.UTF8.GetBytes(text));

    private static string WireText<T>(T value)
    {
        var stream = new MemoryStream();
        LineCodec.WriteAsync(stream, value, CancellationToken.None).GetAwaiter().GetResult();
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    [Fact]
    public async Task Codec_round_trips_and_rejects_long_lines()
    {
        var stream = new MemoryStream();
        await LineCodec.WriteAsync(stream, new HelperRequest(HelperCommand.StopPimax), CancellationToken.None);
        await LineCodec.WriteAsync(stream, new HelperReply(true, WasRunning: true, Value: "x"), CancellationToken.None);
        stream.Position = 0;

        Assert.Equal(new HelperRequest(HelperCommand.StopPimax), await LineCodec.ReadAsync<HelperRequest>(stream, CancellationToken.None));
        Assert.Equal(new HelperReply(true, WasRunning: true, Value: "x"), await LineCodec.ReadAsync<HelperReply>(stream, CancellationToken.None));

        var tooLong = Bytes(new string('a', LineCodec.MaxLineBytes + 1) + "\n");
        await Assert.ThrowsAsync<InvalidDataException>(() => LineCodec.ReadAsync<HelperRequest>(tooLong, CancellationToken.None));
    }

    [Fact]
    public void A_request_is_one_camel_case_line_with_the_command_as_a_word() =>
        Assert.Equal("{\"command\":\"StopPimax\"}\n", WireText(new HelperRequest(HelperCommand.StopPimax)));

    [Fact]
    public void A_reply_is_one_camel_case_line_without_the_empty_fields()
    {
        Assert.Equal("{\"ok\":true,\"wasRunning\":true}\n", WireText(new HelperReply(true, WasRunning: true)));
        Assert.Equal("{\"ok\":false,\"error\":\"No.\"}\n", WireText(new HelperReply(false, "No.")));
    }

    [Fact]
    public async Task A_message_with_a_newline_in_it_stays_on_one_line()
    {
        var text = WireText(new HelperReply(false, "first\nsecond"));

        Assert.Single(text.Split('\n', StringSplitOptions.RemoveEmptyEntries));
        var back = await LineCodec.ReadAsync<HelperReply>(Bytes(text), CancellationToken.None);
        Assert.Equal("first\nsecond", back!.Error);
    }

    [Fact]
    public async Task A_line_of_exactly_the_limit_is_read()
    {
        var json = "{\"command\":\"Ping\"}";
        var line = json + new string(' ', LineCodec.MaxLineBytes - json.Length) + "\n";

        var request = await LineCodec.ReadAsync<HelperRequest>(Bytes(line), CancellationToken.None);

        Assert.Equal(new HelperRequest(HelperCommand.Ping), request);
    }

    [Fact]
    public async Task One_byte_over_the_limit_is_rejected()
    {
        var json = "{\"command\":\"Ping\"}";
        var line = json + new string(' ', LineCodec.MaxLineBytes - json.Length + 1) + "\n";

        await Assert.ThrowsAsync<InvalidDataException>(() => LineCodec.ReadAsync<HelperRequest>(Bytes(line), CancellationToken.None));
    }

    [Fact]
    public async Task Reading_stops_at_the_limit_instead_of_swallowing_a_huge_input()
    {
        var stream = Bytes(new string('a', 100_000));

        await Assert.ThrowsAsync<InvalidDataException>(() => LineCodec.ReadAsync<HelperRequest>(stream, CancellationToken.None));

        Assert.Equal(LineCodec.MaxLineBytes + 1, stream.Position);
    }

    [Fact]
    public async Task Reading_stops_at_the_newline_so_a_second_line_stays_for_the_next_read()
    {
        var stream = Bytes("{\"command\":\"Ping\"}\n{\"command\":\"StopPimax\"}\n");

        Assert.Equal(HelperCommand.Ping, (await LineCodec.ReadAsync<HelperRequest>(stream, CancellationToken.None))!.Command);
        Assert.Equal(HelperCommand.StopPimax, (await LineCodec.ReadAsync<HelperRequest>(stream, CancellationToken.None))!.Command);
    }

    [Fact]
    public async Task A_windows_line_ending_is_fine()
    {
        var request = await LineCodec.ReadAsync<HelperRequest>(Bytes("{\"command\":\"Ping\"}\r\n"), CancellationToken.None);

        Assert.Equal(new HelperRequest(HelperCommand.Ping), request);
    }

    [Fact]
    public async Task A_stream_that_ends_with_nothing_read_gives_null()
    {
        Assert.Null(await LineCodec.ReadAsync<HelperRequest>(new MemoryStream(), CancellationToken.None));
    }

    [Fact]
    public async Task A_stream_that_ends_before_the_newline_is_rejected()
    {
        await Assert.ThrowsAsync<InvalidDataException>(() => LineCodec.ReadAsync<HelperRequest>(Bytes("{\"command\":\"Ping\"}"), CancellationToken.None));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"command\":null}")]
    [InlineData("{\"command\":\"Reboot\"}")]
    [InlineData("{\"command\":3}")]
    [InlineData("{\"command\":\"3\"}")]
    [InlineData("{\"command\":[\"Ping\"]}")]
    public async Task Malformed_requests_are_rejected_with_one_exception_type(string line)
    {
        await Assert.ThrowsAsync<InvalidDataException>(() => LineCodec.ReadAsync<HelperRequest>(Bytes(line + "\n"), CancellationToken.None));
    }

    [Fact]
    public async Task The_json_null_is_read_as_no_message()
    {
        Assert.Null(await LineCodec.ReadAsync<HelperRequest>(Bytes("null\n"), CancellationToken.None));
    }

    [Fact]
    public async Task Extra_properties_are_ignored_not_acted_on()
    {
        var request = await LineCodec.ReadAsync<HelperRequest>(
            Bytes("{\"command\":\"Ping\",\"path\":\"C:\\\\evil.json\"}\n"), CancellationToken.None);

        Assert.Equal(new HelperRequest(HelperCommand.Ping), request);
    }

    [Fact]
    public async Task A_request_cant_carry_a_list()
    {
        // A caller that adds a file list, a path or a SID, at the top or inside the settings, sends only the command and
        // the typed settings: the approved files come from the consent of whoever connected, never from a request.
        const string line =
            "{\"command\":\"StartMonadoElevated\","
            + "\"start\":{\"lighthouseWaitMs\":3000,\"renderScalePercent\":100,\"compositorMinTimeMs\":6,\"exitOnDisconnect\":false,"
            + "\"module\":\"dreamair\",\"approvedFiles\":{\"ArcturusPerception.dll\":\"00\"},\"files\":[\"evil.dll=00\"]},"
            + "\"approvedFiles\":[\"ArcturusPerception.dll=00\"],\"unsignedFiles\":{\"evil.dll\":\"00\"},"
            + "\"steamVrPath\":\"C:\\\\evil\",\"sid\":\"S-1-5-18\"}\n";

        var request = await LineCodec.ReadAsync<HelperRequest>(Bytes(line), CancellationToken.None);

        Assert.Equal(new HelperRequest(HelperCommand.StartMonadoElevated, new ElevatedStart(3000, 100, 6, false, "dreamair")), request);

        // Nor could a later field hold one: a request is a command word, settings of plain numbers, switches and the
        // module's name, and a version's name (InstallMonado), which the helper looks up in Mirador's own release list.
        // A profile's id (SetRuntimeApproved), which the helper looks up in its own record of approved builds.
        Assert.Equal(["Build", "Command", "Start", "Version"], typeof(HelperRequest).GetProperties().Select(p => p.Name).Order());
        Assert.Equal(typeof(string), typeof(HelperRequest).GetProperty("Version")!.PropertyType);
        Assert.Equal(typeof(string), typeof(HelperRequest).GetProperty("Build")!.PropertyType);
        foreach (var property in typeof(ElevatedStart).GetProperties())
        {
            Assert.True(
                property.PropertyType == typeof(int) || property.PropertyType == typeof(bool) || property.PropertyType == typeof(string),
                $"ElevatedStart.{property.Name} is a {property.PropertyType.Name}");
        }
    }

    [Fact]
    public async Task A_refusal_reason_goes_over_the_wire_as_a_word()
    {
        var reply = new HelperReply(false, "SteamVR changed (ArcturusPerception.dll).", Refusal: HelperRefusal.SteamVrChanged);

        var text = WireText(reply);

        Assert.Contains("\"refusal\":\"SteamVrChanged\"", text);
        Assert.Equal(reply, await LineCodec.ReadAsync<HelperReply>(Bytes(text), CancellationToken.None));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            LineCodec.ReadAsync<HelperReply>(Bytes("{\"ok\":false,\"refusal\":0}\n"), CancellationToken.None));
    }

    [Fact]
    public async Task A_value_too_long_for_one_line_is_refused_on_write()
    {
        var stream = new MemoryStream();

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            LineCodec.WriteAsync(stream, new HelperReply(false, new string('x', LineCodec.MaxLineBytes)), CancellationToken.None));

        Assert.Equal(0, stream.Length);
    }

    [Fact]
    public async Task A_cancelled_read_stops()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            LineCodec.ReadAsync<HelperRequest>(Bytes("{\"command\":\"Ping\"}\n"), cts.Token));
    }
}
