using VendorFunding.Domain;

namespace VendorFunding.Domain.Tests;

public class SalesFileParserTests
{
    private const string Header = "store_number,sku,sale_date,units_sold,net_sales";

    [Fact]
    public void Parses_valid_rows()
    {
        var result = Parse($"""
            {Header}
            S0001,100245,2026-11-13,42,209.58
            S0002, 100246 ,2026-11-13,-3,-19.47
            """);

        Assert.Null(result.FileError);
        Assert.Empty(result.Errors);
        Assert.Equal(
            [
                new ParsedSalesRow("S0001", "100245", new DateOnly(2026, 11, 13), 42, 209.58m),
                new ParsedSalesRow("S0002", "100246", new DateOnly(2026, 11, 13), -3, -19.47m),
            ],
            result.Rows);
    }

    [Fact]
    public void Collects_bad_rows_with_line_numbers_and_keeps_the_good_ones()
    {
        var result = Parse($"""
            {Header}
            S0001,100245,2026-11-13,42,209.58
            S0001,100245,11/13/2026,1,4.99
            S0001,,2026-11-13,1,4.99
            S0001,100245,2026-11-13,1.5,4.99
            S0001,100245,2026-11-13,1,4.999
            S0001,100245,2026-11-13,1
            S0001,100246,2026-11-13,7,45.43
            """);

        Assert.Equal(2, result.Rows.Count);
        Assert.Collection(result.Errors,
            e => Assert.Equal((3, "sale_date"), (e.LineNumber, e.Error.Split(' ')[0])),
            e => Assert.Equal((4, "sku"), (e.LineNumber, e.Error.Split(' ')[0])),
            e => Assert.Equal((5, "units_sold"), (e.LineNumber, e.Error.Split(' ')[0])),
            e => Assert.Equal((6, "net_sales"), (e.LineNumber, e.Error.Split(' ')[0])),
            e => Assert.Equal((7, "Expected"), (e.LineNumber, e.Error.Split(' ')[0])));
        Assert.Equal("S0001,100245,11/13/2026,1,4.99", result.Errors[0].Line);
    }

    [Fact]
    public void Rejects_values_that_are_too_long()
    {
        var result = Parse($"{Header}\nSTORE-NUMBER-TOO-LONG,100245,2026-11-13,1,4.99\nS1,SKU-THAT-IS-WAY-TOO-LONG-1,2026-11-13,1,4.99");

        Assert.Empty(result.Rows);
        Assert.Equal(2, result.Errors.Count);
    }

    [Fact]
    public void Skips_blank_lines_and_accepts_a_byte_order_mark()
    {
        var result = Parse($"﻿{Header}\n\nS0001,100245,2026-11-13,1,4.99\n\n");

        Assert.Single(result.Rows);
        Assert.Empty(result.Errors);
    }

    [Theory]
    [InlineData("", "empty")]
    [InlineData("store,sku,date,units,sales\nS1,1,2026-11-13,1,1", "header")]
    public void Fails_the_whole_file_without_the_expected_header(string content, string expectedWord)
    {
        var result = Parse(content);

        Assert.NotNull(result.FileError);
        Assert.Contains(expectedWord, result.FileError);
        Assert.Empty(result.Rows);
    }

    private static SalesFileParseResult Parse(string content) => SalesFileParser.Parse(new StringReader(content));
}
