// SPDX-License-Identifier: AGPL-3.0-only

using CouchSubs.Core;

namespace CouchSubs.Core.Tests;

public class ReadingLineHeightTests
{
    [Fact]
    public void BoxCarriesMedianLineHeightIntoTheScreen()
    {
        var reading = new Reading([
            new TextLine("Where are you going?", new TextBox(0.2, 0.1, 0.8, 0.4)),
            new TextLine("Home.", new TextBox(0.4, 0.5, 0.6, 0.8)),
            new TextLine("Now.", new TextBox(0.4, 0.85, 0.6, 1.0)),
        ]);
        var area = new TextBox(0, 0.8, 1, 1.0);

        var box = reading.Box!.Value.PlacedIn(area);

        Assert.Equal(0.06, box.Line, 3);
        Assert.Equal(0.82, box.Top, 3);
        Assert.Equal(new TextBox(0.2, 0.1, 0.8, 1.0), reading.Box!.Value with { Line = 9 });
    }
}
