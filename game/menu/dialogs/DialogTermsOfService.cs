using Godot;

namespace VortexArena.Game.Menu;

/// <summary>
/// "Terms of Service" — <c>XonoticToSDialog_fill</c> (qcsrc/menu/xonotic/dialog_termsofservice.qc) on its 16 x 6.2
/// grid, 0.8 of the menu wide: the wrapped subtitle (the "updated" wording once a version was accepted), a blank
/// row, the colour-coded text box over the next 12 rows, then a green "Accept" and a red "Don't accept (quit the
/// game)" sharing the last row.
///
/// The QC fills the text box from <c>https://update.xonotic.org/tos.txt</c> (url_single_fopen, XonoticToS_OnGet).
/// The port has no ToS fetch, so the box stays empty, as Xonotic's does until the download returns.
/// Accept records <c>_termsofservice_accepted</c> (QC: the fetched version, <c>_Nex_ExtResponseSystem_NewToS</c>;
/// with none known, "1") and saves the config; "Don't accept" runs <c>quit</c>, as Xonotic does.
/// </summary>
public partial class DialogTermsOfService : MenuScreen, IQcDialog
{
    // dialog_termsofservice.qh (name "TermsOfService", closable false)
    public string QcTitle => Localization.Tr("Terms of Service");
    public float QcIntendedWidth => 0.8f;
    public float QcRows => 16;
    public Color QcBorderColor => MenuSkin.SkinColor("COLOR_DIALOG_FIRSTRUN", 1f, 1f, 1f);

    private QcTextBox _textBox = null!;

    protected override void BuildUi()
    {
        Name = "DialogTermsOfService";

        var me = new QcGrid(QcRows, 6.2f);
        me.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(me);

        string subtitle = MenuState.Cvars.GetFloat("_termsofservice_accepted") > 0f
            ? "Terms of Service have been updated. Please read them before continuing:"
            : "Welcome to Xonotic! Please read the following Terms of Service:";

        me.TR();
            me.TD(1, 5, new QcTextLabel(subtitle) { AllowWrap = true });

        me.TR();
        me.TR();
            _textBox = me.TD(me.Rows - 4, me.Columns, new QcTextBox { AllowColors = true });

        me.TR();
        me.GotoRC(me.Rows - 1, 0);
            me.TD(1, me.Columns / 2, Qc.Button("Accept", OnAccept, tint: new Color(0, 1, 0)));
            me.TD(1, me.Columns / 2, Qc.Button("Don't accept (quit the game)", () => MenuCommand.Run("quit"), tint: new Color(1, 0, 0)));
    }

    /// <summary>XonoticToS_OnGet: the downloaded text, for whoever fetches it.</summary>
    public void SetText(string text)
    {
        if (_textBox is not null) _textBox.Text = text;
    }

    // QC Close_Clicked: cvar_set("_termsofservice_accepted", ftos(_Nex_ExtResponseSystem_NewToS)); saveconfig.
    private void OnAccept()
    {
        MenuState.Cvars.Set("_termsofservice_accepted", "1");
        MenuState.Cvars.MarkArchived("_termsofservice_accepted");
        MenuState.SaveUserConfig();
        GoBack();
    }
}
