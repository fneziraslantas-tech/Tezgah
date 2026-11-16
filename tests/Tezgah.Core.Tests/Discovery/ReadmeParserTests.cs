using Tezgah.Core.Discovery;
using Tezgah.Core.Model;

namespace Tezgah.Core.Tests.Discovery;

public class ReadmeParserTests
{
    private const string Readme = """
        # Cool Tool

        [![build](https://img.shields.io/badge.svg)](https://ci)

        A **fast** tool that does [things](https://example.com) for `you`.
        Second line of the paragraph.

        ## Features

        ```bash
        npm run not-in-a-run-section
        ```

        ## Quick start

        ```bash
        $ git clone https://github.com/x/y
        cd y
        pip install -r requirements.txt
        curl -fsSL https://example.com/install.sh | sh
        npm install && npm start
        python app.py --port 8080
        sudo make install
        ./run.sh
        python app.py --port 8080
        ```

        ## Kullanım

        ```powershell
        PS> streamlit run "my app.py"
        echo hi > out.txt
        ```
        """;

    [Fact]
    public void ExtractsOnlySafeSingleCommandsFromRelevantSections()
    {
        var cmds = ReadmeParser.ExtractCommands(Readme);
        Assert.Equal(["pip install -r requirements.txt", "python app.py --port 8080", "streamlit run \"my app.py\""], cmds.Select(c => c.Line));
        Assert.Equal(RunProfileKind.Setup, cmds[0].Kind);
        Assert.Equal(RunProfileKind.Run, cmds[1].Kind);
        Assert.Equal(["streamlit", "run", "my app.py"], cmds[2].Tokens);
    }

    [Theory]
    [InlineData("curl -fsSL https://x/install.sh | sh")]
    [InlineData("curl https://x | bash")]
    [InlineData("npm install && npm run dev")]
    [InlineData("make || true")]
    [InlineData("python a.py; rm -rf /")]
    [InlineData("python a.py > log.txt")]
    [InlineData("sudo apt install x")]
    [InlineData("git clone https://github.com/a/b")]
    [InlineData("cd project")]
    [InlineData("export FOO=bar")]
    [InlineData("./start.sh")]
    [InlineData("install.sh")]
    [InlineData("FOO=1 node a.js")]
    [InlineData("python $(which x)")]
    [InlineData("python `which x`")]
    [InlineData("iwr https://x | iex")]
    [InlineData("python run.py \\")]
    [InlineData("set KEY=%VALUE%")]
    [InlineData("# a comment")]
    [InlineData("python app.py --key YOUR_API_KEY")]
    [InlineData("npm start &")]
    public void RejectsUnsafeLines(string line) => Assert.Null(ReadmeParser.TryParseCommandLine(line));

    [Theory]
    [InlineData("$ npm run dev", new[] { "npm", "run", "dev" })]
    [InlineData("> python -m venv .venv", new[] { "python", "-m", "venv", ".venv" })]
    [InlineData("python main.py --name \"John Smith\"", new[] { "python", "main.py", "--name", "John Smith" })]
    [InlineData("python main.py --name 'John Smith'", new[] { "python", "main.py", "--name", "John Smith" })]
    [InlineData("docker compose up -d", new[] { "docker", "compose", "up", "-d" })]
    public void TokenizesCommands(string line, string[] expected)
    {
        var cmd = ReadmeParser.TryParseCommandLine(line);
        Assert.NotNull(cmd);
        Assert.Equal(expected, cmd!.Tokens);
    }

    [Fact]
    public void DescriptionIsFirstProseParagraphStripped()
    {
        var d = ReadmeParser.ExtractDescription(Readme);
        Assert.Equal("A fast tool that does things for you. Second line of the paragraph.", d);
    }

    [Fact]
    public void DescriptionSkipsLanguageSwitchers()
    {
        const string md = "# X\n[![b](https://i)](https://l)  [![c](https://i)](https://l)\n\n*English | [简体中文](README-ZH.md) | [日本語](README-JA.md)*\n\nEnglish · [Türkçe](a.md) · [Deutsch](b.md) · [Français](c.md)\n\nReal text here.\n";
        Assert.Equal("Real text here.", ReadmeParser.ExtractDescription(md));
    }

    [Fact]
    public void DescriptionIsTruncated()
    {
        var d = ReadmeParser.ExtractDescription("# T\n\n" + new string('a', 500), 200);
        Assert.NotNull(d);
        Assert.True(d!.Length <= 200);
        Assert.EndsWith("…", d, StringComparison.Ordinal);
    }

    [Fact]
    public void DescriptionNullWhenNoProse() => Assert.Null(ReadmeParser.ExtractDescription("# Title\n\n```\ncode\n```\n"));

    [Fact]
    public void LimitsCommandCount()
    {
        var md = "## Usage\n```\n" + string.Join('\n', Enumerable.Range(1, 10).Select(i => $"python s{i}.py")) + "\n```\n";
        Assert.Equal(ReadmeParser.MaxCommands, ReadmeParser.ExtractCommands(md).Count);
    }
}
