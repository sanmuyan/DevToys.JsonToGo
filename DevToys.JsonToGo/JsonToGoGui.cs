using DevToys.Api;
using System.ComponentModel.Composition;
using static DevToys.Api.GUI;
using DevToys.JsonToGo.Converters;

namespace DevToys.JsonToGo;

[Export(typeof(IGuiTool))]
[Name("JsonToGo")]
[ToolDisplayInformation(
    IconFontName = "FluentSystemIcons",
    IconGlyph = '\uf39a',
    GroupName = PredefinedCommonToolGroupNames.Converters,
    ResourceManagerAssemblyIdentifier = nameof(JsonToGoAssemblyIdentifier),
    ResourceManagerBaseName = "DevToys.JsonToGo.JsonToGoExtension",
    ShortDisplayTitleResourceName = nameof(JsonToGoExtension.ShortDisplayTitle),
    LongDisplayTitleResourceName = nameof(JsonToGoExtension.LongDisplayTitle),
    DescriptionResourceName = nameof(JsonToGoExtension.Description),
    AccessibleNameResourceName = nameof(JsonToGoExtension.AccessibleName))]
internal sealed class JsonToGoGui : IGuiTool
{
    private readonly IUIMultiLineTextInput _inputTextArea = MultiLineTextInput("json-to-go-input-text-area");
    private readonly IUIMultiLineTextInput _outputTextArea = MultiLineTextInput("json-to-go-output-text-area");

    private enum GridRows
    {
        Header,
        Content,
        Footer
    }

    private enum GridColumns
    {
        Content
    }


    public UIToolView View =>
        new UIToolView(
            isScrollable: true,
            Grid()
                .ColumnLargeSpacing()
                .RowLargeSpacing()
                .Rows(
                    (GridRows.Header, Auto),
                    (GridRows.Content, new UIGridLength(1, UIGridUnitType.Fraction))
                )
                .Columns(
                    (GridColumns.Content, new UIGridLength(1, UIGridUnitType.Fraction))
                )
                .Cells(
                    Cell(
                        GridRows.Content,
                        GridColumns.Content,
                        SplitGrid()
                            .Vertical()
                            .WithLeftPaneChild(
                                _inputTextArea
                                    .Title("Input(JSON)")
                                    .Language("json")
                                    .OnTextChanged(OnInputTextChanged)
                            )
                            .WithRightPaneChild(
                                _outputTextArea
                                    .Title("Output(GO)")
                                    .Language("go")
                                    .ReadOnly()
                                    .Extendable()
                            )
                    ))
        );

    public void OnDataReceived(string dataTypeName, object? parsedData)
    {
        throw new NotImplementedException();
    }

    private void OnInputTextChanged(string inputText)
    {
        Convert(inputText);
    }

    private void Convert(string inputText)
    {
        if (string.IsNullOrEmpty(inputText))
        {
            _outputTextArea.Text(string.Empty);
            return;
        }

        try
        {
            var converter = new JsonToGoConverter(inputText);
            _outputTextArea.Text(converter.Convert());
        }
        catch
        {
            _outputTextArea.Text("Please provide a valid JSON");
        }
    }
}