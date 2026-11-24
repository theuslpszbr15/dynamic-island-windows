using System.Text.Json.Nodes;
using DynamicIsland.Services;
using Xunit;
using Drawing = System.Drawing;

namespace DynamicIsland.Tests;

public class ComputerUseTests
{
    static ComputerAction? Parse(string json) => ComputerUse.Parse(JsonNode.Parse(json) as JsonObject);

    [Fact]
    public void ParsesOnlyAllowedActions()
    {
        Assert.Equal(new ComputerAction("clicar", 10, 20, "", "", "abrir"), Parse("""{"acao":"clicar","x":10,"y":"20","motivo":"abrir"}"""));
        Assert.Equal("enter", Parse("""{"acao":"tecla","tecla":"Enter"}""")?.Key);
        Assert.Equal("ctrl+a", Parse("""{"acao":"tecla","tecla":"Ctrl + A"}""")?.Key);
        Assert.Equal("cima", Parse("""{"acao":"rolar","tecla":"up"}""")?.Key);
        Assert.Equal("baixo", Parse("""{"acao":"rolar"}""")?.Key);
        Assert.Equal(-1, Parse("""{"acao":"clicar_texto","texto":" Salvar ","x":5,"y":5}""")?.X);
        // Esc is the person's stop key; Alt+F4 and anything else isn't on the list
        Assert.Null(Parse("""{"acao":"tecla","tecla":"esc"}"""));
        Assert.Null(Parse("""{"acao":"tecla","tecla":"alt+f4"}"""));
        Assert.Null(Parse("""{"acao":"executar","texto":"format c:"}"""));
        Assert.Null(Parse("""{"acao":"clicar"}"""));
        Assert.Null(Parse("""{"acao":"digitar","texto":""}"""));
        Assert.Null(ComputerUse.Parse(null));
    }

    [Fact]
    public void AsksBeforeDangerousSteps()
    {
        Assert.True(ComputerUse.IsRisky(Parse("""{"acao":"digitar","texto":"oi"}""")!, null));
        Assert.True(ComputerUse.IsRisky(Parse("""{"acao":"tecla","tecla":"enter"}""")!, null));
        Assert.False(ComputerUse.IsRisky(Parse("""{"acao":"tecla","tecla":"tab"}""")!, null));
        Assert.True(ComputerUse.IsRisky(Parse("""{"acao":"clicar_texto","texto":"Finalizar compra"}""")!, null));
        Assert.True(ComputerUse.IsRisky(Parse("""{"acao":"clicar","x":1,"y":1}""")!, "EXCLUIR conta"));
        Assert.True(ComputerUse.IsRisky(Parse("""{"acao":"clicar","x":1,"y":1,"motivo":"enviar o e-mail"}""")!, "Ícone"));
        Assert.False(ComputerUse.IsRisky(Parse("""{"acao":"clicar_texto","texto":"Playlists"}""")!, null));
        Assert.False(ComputerUse.IsRisky(Parse("""{"acao":"rolar","tecla":"baixo"}""")!, null));
    }

    [Fact]
    public void NeverTypesSecrets()
    {
        Assert.True(ComputerUse.LooksSecret(Parse("""{"acao":"digitar","texto":"x","motivo":"preencher a senha"}""")!, null));
        Assert.True(ComputerUse.LooksSecret(Parse("""{"acao":"digitar","texto":"123456"}""")!, "Código de verificação"));
        Assert.False(ComputerUse.LooksSecret(Parse("""{"acao":"digitar","texto":"lofi"}""")!, "Pesquisar"));
    }

    [Fact]
    public void FindsTextOnTheScreen()
    {
        var lines = new List<(string, Drawing.RectangleF)>
        {
            ("Arquivo Editar Exibir", new Drawing.RectangleF(0, 0, 210, 20)),
            ("Salvar", new Drawing.RectangleF(300, 100, 60, 20)),
            ("Salvar como…", new Drawing.RectangleF(300, 140, 120, 20)),
        };
        var exact = ComputerUse.Locate(lines, "salvar");
        Assert.Equal(new Drawing.PointF(330, 110), exact?.Point);
        // part of a line: about where the word sits
        var part = ComputerUse.Locate(lines, "Editar");
        Assert.InRange(part!.Value.Point.X, 80, 130);
        Assert.Null(ComputerUse.Locate(lines, "Imprimir"));
        Assert.Equal("Salvar", ComputerUse.Near(lines, new Drawing.PointF(330, 125)));
        Assert.Null(ComputerUse.Near(lines, new Drawing.PointF(900, 900)));
    }

    [Fact]
    public void DescribesForTheApproval()
    {
        Assert.Equal("Clicar em “Enviar”", ComputerUse.Describe(Parse("""{"acao":"clicar_texto","texto":"Enviar"}""")!, "Enviar"));
        Assert.Equal("Digitar “lofi”", ComputerUse.Describe(Parse("""{"acao":"digitar","texto":"lofi"}""")!, null));
        Assert.Equal("Apertar enter", ComputerUse.Describe(Parse("""{"acao":"tecla","tecla":"enter"}""")!, null));
    }

    [Fact]
    public void FindsTheWindowTheTaskNames()
    {
        var windows = new List<(IntPtr, string)>
        {
            (1, "GM Industrial Engineering - Google Chrome"),
            (2, "Teste da Ilha"),
            (3, "notas.txt - Bloco de Notas"),
            (4, "Spotify Premium"),
        };
        Assert.Equal((IntPtr)2, ComputerUse.NamedWindow("Na janela Teste da Ilha, escreva oi", windows));
        Assert.Equal((IntPtr)3, ComputerUse.NamedWindow("no bloco de notas escreva oi", windows));
        Assert.Equal((IntPtr)1, ComputerUse.NamedWindow("abre uma aba nova no Google Chrome", windows));
        Assert.Null(ComputerUse.NamedWindow("toca a playlist Foco", windows));
    }

    [Fact]
    public void NoticesWhenTheScreenDidNotChange()
    {
        using var screen = new Drawing.Bitmap(1280, 720);
        using (var g = Drawing.Graphics.FromImage(screen)) g.Clear(Drawing.Color.FromArgb(30, 30, 30));
        var before = ComputerUse.Thumbprint(screen);
        // a clock ticking in a corner is no change
        using (var g = Drawing.Graphics.FromImage(screen)) g.FillRectangle(Drawing.Brushes.White, 1240, 700, 30, 15);
        Assert.True(ComputerUse.Unchanged(before, ComputerUse.Thumbprint(screen)));
        // a window opening is
        using (var g = Drawing.Graphics.FromImage(screen)) g.FillRectangle(Drawing.Brushes.White, 300, 200, 600, 300);
        Assert.False(ComputerUse.Unchanged(before, ComputerUse.Thumbprint(screen)));
        Assert.False(ComputerUse.Unchanged(null, before));
    }

    /// <summary>VISION_E2E=1 (Groq key on this PC): the vision model looks at a drawn window and picks the right button.</summary>
    [Fact]
    public async Task VisionModelPicksTheButton()
    {
        if (Environment.GetEnvironmentVariable("VISION_E2E") != "1" || !JarvisBrain.HasKey) return;
        using var shot = new Drawing.Bitmap(1280, 720);
        using (var g = Drawing.Graphics.FromImage(shot))
        {
            g.Clear(Drawing.Color.FromArgb(243, 243, 243));
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            using var title = new Drawing.Font("Segoe UI", 20);
            using var font = new Drawing.Font("Segoe UI", 14);
            g.DrawString("Bloco de Notas - Salvar alterações em Sem título?", title, Drawing.Brushes.Black, 300, 250);
            foreach (var (text, x) in new[] { ("Salvar", 420), ("Não salvar", 590), ("Cancelar", 780) })
            {
                g.FillRectangle(Drawing.Brushes.White, x, 340, 150, 44);
                g.DrawRectangle(Drawing.Pens.Gray, x, 340, 150, 44);
                g.DrawString(text, font, Drawing.Brushes.Black, x + 22, 349);
            }
        }
        var lines = await ScreenOcr.ReadLinesAsync(shot) ?? [];
        var json = await JarvisBrain.VisionJsonAsync(ComputerUse.SystemPrompt,
            ComputerUse.StepText("Fechar sem salvar as alterações", 1, [], lines, 1, shot.Width, shot.Height), ComputerUse.Jpeg(shot, 1));
        var action = ComputerUse.Parse(json);
        Assert.True(action is { IsClick: true }, "resposta: " + json?.ToJsonString());
        var point = action!.Kind == "clicar_texto" || action.X < 0
            ? ComputerUse.Locate(lines, action.Text)?.Point
            : new Drawing.PointF(action.X, action.Y);
        Assert.NotNull(point);
        Assert.InRange(point!.Value.X, 580, 750);
        Assert.InRange(point.Value.Y, 330, 395);
    }
}
