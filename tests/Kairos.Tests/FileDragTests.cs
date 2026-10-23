using System.Windows;
using Kairos.Ui;

namespace Kairos.Tests;

/// <summary>Clips dragged out of Kairos (into Discord or a folder): a click must stay a click.</summary>
public static class FileDragTests
{
    [Test]
    public static void A_small_move_is_still_a_click()
    {
        Assert.True(!FileDrag.Passed(new Vector(2, 2)), "a hand that shakes while clicking");
        Assert.True(FileDrag.Passed(new Vector(40, 0)), "a drag");
        Assert.True(FileDrag.Passed(new Vector(0, -40)), "upwards too");
    }
}
