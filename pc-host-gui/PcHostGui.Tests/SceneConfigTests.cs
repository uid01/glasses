using System.Text.Json;
using PcHostGui.Models;
using Xunit;

namespace PcHostGui.Tests;

public class SceneConfigTests
{
    [Fact]
    public void NewSceneConfig_StartsEmpty()
    {
        var scene = new SceneConfig();

        Assert.Empty(scene.Objects);
        Assert.False(scene.IsComplete); // no objects at all -- nothing to render
    }

    [Fact]
    public void IsComplete_FalseWhileAnyObjectUnassigned()
    {
        var scene = new SceneConfig();
        scene.AddObject();

        Assert.False(scene.IsComplete);
    }

    [Fact]
    public void IsComplete_TrueOnceEveryObjectHasASource()
    {
        var scene = new SceneConfig();
        var obj = scene.AddObject();
        obj.OutputIndex = 0;

        Assert.True(scene.IsComplete);
    }

    [Fact]
    public void ToSceneFileJson_Throws_WhenAnyObjectUnassigned()
    {
        var scene = new SceneConfig();
        scene.AddObject();

        Assert.Throws<InvalidOperationException>(() => scene.ToSceneFileJson());
    }

    [Fact]
    public void ToSceneFileJson_Throws_WhenNoObjectsAtAll()
    {
        var scene = new SceneConfig();

        Assert.Throws<InvalidOperationException>(() => scene.ToSceneFileJson());
    }

    [Fact]
    public void AddObject_FansPanelsOutLeftAndRight_InsteadOfStackingAtOrigin()
    {
        var scene = new SceneConfig();
        var first = scene.AddObject();
        var second = scene.AddObject();
        var third = scene.AddObject();

        Assert.Equal(0f, first.PosX);
        Assert.NotEqual(second.PosX, third.PosX);
        Assert.NotEqual(0f, second.PosX);
        Assert.NotEqual(0f, third.PosX);
    }

    /// <summary>
    /// Locks down the exact JSON property names pc-host's --scene-file loader expects
    /// (pc-host/Render/SceneFileFormat.cs's SceneFileDto/SceneObjectFileDto, deserialized with
    /// JsonSerializerDefaults.Web -- i.e. camelCase). A mismatch here would silently produce a
    /// scene file pc-host either rejects or parses with all-default values.
    /// </summary>
    [Fact]
    public void ToSceneFileJson_UsesCamelCasePropertyNames_MatchingPcHostSceneFileFormat()
    {
        var scene = new SceneConfig { Width = 2560, Height = 1080 };
        var obj = scene.AddObject();
        obj.OutputIndex = 3;
        obj.PanelWidth = 1.5f;
        obj.PanelHeight = 0.9f;
        obj.CurvatureDegrees = 45f;
        obj.PosX = -0.5f;
        obj.PosY = 0.2f;
        obj.PosZ = 1.7f;
        obj.YawDegrees = 10f;
        obj.PitchDegrees = -5f;
        obj.RollDegrees = 1f;

        string json = scene.ToSceneFileJson();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal(2560, root.GetProperty("width").GetInt32());
        Assert.Equal(1080, root.GetProperty("height").GetInt32());

        var objects = root.GetProperty("objects");
        Assert.Equal(1, objects.GetArrayLength());

        var jsonObj = objects[0];
        Assert.Equal(3, jsonObj.GetProperty("outputIndex").GetInt32());
        Assert.Equal(1.5, jsonObj.GetProperty("panelWidth").GetDouble(), 3);
        Assert.Equal(0.9, jsonObj.GetProperty("panelHeight").GetDouble(), 3);
        Assert.Equal(45, jsonObj.GetProperty("curvatureDegrees").GetDouble(), 3);
        Assert.Equal(-0.5, jsonObj.GetProperty("posX").GetDouble(), 3);
        Assert.Equal(0.2, jsonObj.GetProperty("posY").GetDouble(), 3);
        Assert.Equal(1.7, jsonObj.GetProperty("posZ").GetDouble(), 3);
        Assert.Equal(10, jsonObj.GetProperty("yawDegrees").GetDouble(), 3);
        Assert.Equal(-5, jsonObj.GetProperty("pitchDegrees").GetDouble(), 3);
        Assert.Equal(1, jsonObj.GetProperty("rollDegrees").GetDouble(), 3);
    }

    [Fact]
    public void SceneObjectConfig_Label_ReflectsAssignmentState()
    {
        var obj = new SceneObjectConfig();
        Assert.Equal("(unassigned)", obj.Label);

        obj.OutputIndex = 2;
        Assert.Equal("Monitor #2", obj.Label);
    }
}
