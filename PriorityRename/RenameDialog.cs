namespace PriorityRename;

public sealed class RenameDialog : Form
{
    private readonly TextBox _nameBox = new() { Width = 330 };
    private readonly CheckBox _priority = new() { Text = "Override / Take Priority (Shift existing files)", Checked = true, AutoSize = true };
    private readonly Button _cancel = new() { Text = "Cancel", DialogResult = DialogResult.Cancel };
    private readonly Button _ok = new() { Text = "Rename", DialogResult = DialogResult.OK };

    public string TargetName => _nameBox.Text.Trim();
    public bool TakePriority => _priority.Checked;

    public RenameDialog(string currentName)
    {
        Text = "Rename";
        FormBorderStyle = FormBorderStyle.FixedToolWindow;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(360, 118);
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        TopMost = true;
        AcceptButton = _ok;
        CancelButton = _cancel;

        var label = new Label
        {
            Text = "New name:",
            Location = new Point(12, 10),
            AutoSize = true
        };
        _nameBox.Location = new Point(12, 31);
        _priority.Location = new Point(12, 58);
        _ok.Location = new Point(200, 86);
        _cancel.Location = new Point(278, 86);

        Controls.AddRange([label, _nameBox, _priority, _ok, _cancel]);
        _nameBox.Text = currentName;
        Shown += (_, _) =>
        {
            _nameBox.Focus();
            var dot = currentName.LastIndexOf('.');
            _nameBox.SelectionStart = 0;
            _nameBox.SelectionLength = dot > 0 ? dot : currentName.Length;
        };
    }
}
