using Shapeshift.Engine.Tests.Support;

namespace Shapeshift.Engine.Tests;

/// <summary>Схема не должна пропускать всё подряд: иначе проверка в остальных тестах ничего не стоит.</summary>
public sealed class AnswerFileSchemaTests : IDisposable
{
    readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    const string Envelope = """
        <unattend xmlns="urn:schemas-microsoft-com:unattend" xmlns:wcm="http://schemas.microsoft.com/WMIConfig/2002/State">
          <settings pass="{0}">
            <component name="Microsoft-Windows-Setup" processorArchitecture="amd64" publicKeyToken="31bf3856ad364e35" language="neutral" versionScope="nonSxS">
              <RunSynchronous>
                {1}
              </RunSynchronous>
            </component>
          </settings>
        </unattend>
        """;

    const string Command = """
        <RunSynchronousCommand wcm:action="add"><Order>{0}</Order><Path>cmd.exe</Path></RunSynchronousCommand>
        """;

    [Theory]
    [InlineData("windowsPE", "1", "2", true)]
    [InlineData("windowsPe", "1", "2", false)]
    [InlineData("windowsPE", "0", "1", false)]
    [InlineData("windowsPE", "1", "1", false)]
    public void Validates_pass_and_commands(string pass, string order1, string order2, bool valid)
    {
        var xml = string.Format(Envelope, pass,
            string.Format(Command, order1) + string.Format(Command, order2));
        var path = _dir.Write("autounattend.xml", xml);

        Assert.Equal(valid, AnswerFileSchema.Validate(path).Count == 0);
    }
}
