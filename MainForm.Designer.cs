namespace SlinnerBMusicStudio;

partial class MainForm
{
    private System.ComponentModel.IContainer components = null!;

    private MenuStrip menuStrip;
    private ToolStripMenuItem miSave;
    private ToolStripMenuItem miExport;
    private ToolStripMenuItem miUndo;
    private ToolStripMenuItem miRedo;
    private ToolStripMenuItem miCut;
    private ToolStripMenuItem miCopy;
    private ToolStripMenuItem miPaste;
    private ToolStripMenuItem miDelete;
    private ToolStripMenuItem miSelectAll;
    private ToolStripMenuItem miTrim;
    private ToolStripMenuItem miSilence;
    private ToolStripMenuItem miNormalize;
    private ToolStripMenuItem miLoudness;
    private ToolStripMenuItem miAmplify;
    private ToolStripMenuItem miFadeIn;
    private ToolStripMenuItem miFadeOut;
    private ToolStripMenuItem miRemoveTrack;

    private Panel transportPanel;
    private Button recordButton;
    private Button stopButton;
    private Button playButton;
    private Button replayButton;
    private Button addTrackButton;
    private Button removeTrackButton;
    private Button zoomOutButton;
    private Button zoomInButton;
    private Button zoomFitButton;

    private Panel devicePanel;
    private Label micLabel;
    private ComboBox micCombo;
    private Label speakerLabel;
    private ComboBox speakerCombo;
    private Button refreshButton;
    private Label levelLabel;
    private ProgressBar levelBar;

    private Panel editPanel;
    private Button undoButton;
    private Button redoButton;
    private Button cutButton;
    private Button copyButton;
    private Button pasteButton;
    private Button deleteButton;
    private Button trimButton;
    private Button silenceButton;
    private Label timeLabel;

    private WaveformView waveform;
    private StatusStrip statusStrip;
    private ToolStripStatusLabel statusLabel;
    private System.Windows.Forms.Timer levelTimer;
    private System.Windows.Forms.Timer playTimer;
    private ToolTip toolTip;

    protected override void Dispose(bool disposing)
    {
        if (disposing && components != null) components.Dispose();
        base.Dispose(disposing);
    }

    private void InitializeComponent()
    {
        this.components = new System.ComponentModel.Container();

        this.menuStrip = new MenuStrip();
        this.transportPanel = new Panel();
        this.recordButton = new Button();
        this.stopButton = new Button();
        this.playButton = new Button();
        this.replayButton = new Button();
        this.addTrackButton = new Button();
        this.removeTrackButton = new Button();
        this.zoomOutButton = new Button();
        this.zoomInButton = new Button();
        this.zoomFitButton = new Button();
        this.devicePanel = new Panel();
        this.micLabel = new Label();
        this.micCombo = new ComboBox();
        this.speakerLabel = new Label();
        this.speakerCombo = new ComboBox();
        this.refreshButton = new Button();
        this.levelLabel = new Label();
        this.levelBar = new ProgressBar();
        this.editPanel = new Panel();
        this.undoButton = new Button();
        this.redoButton = new Button();
        this.cutButton = new Button();
        this.copyButton = new Button();
        this.pasteButton = new Button();
        this.deleteButton = new Button();
        this.trimButton = new Button();
        this.silenceButton = new Button();
        this.timeLabel = new Label();
        this.waveform = new WaveformView();
        this.statusStrip = new StatusStrip();
        this.statusLabel = new ToolStripStatusLabel();
        this.levelTimer = new System.Windows.Forms.Timer(this.components);
        this.playTimer = new System.Windows.Forms.Timer(this.components);
        this.toolTip = new ToolTip(this.components);

        // --- menu -----------------------------------------------------------
        var fileMenu = new ToolStripMenuItem("&File");
        var miNew = new ToolStripMenuItem("&New", null, this.New_Click) { ShortcutKeys = Keys.Control | Keys.N };
        var miOpen = new ToolStripMenuItem("&Open…", null, this.Open_Click) { ShortcutKeys = Keys.Control | Keys.O };
        this.miSave = new ToolStripMenuItem("&Save Mix as WAV…", null, this.SaveWav_Click) { ShortcutKeys = Keys.Control | Keys.S };
        this.miExport = new ToolStripMenuItem("&Export Mix as MP3…", null, this.ExportMp3_Click);
        var miExit = new ToolStripMenuItem("E&xit", null, this.Exit_Click);
        fileMenu.DropDownItems.AddRange(new ToolStripItem[]
        {
            miNew, miOpen, new ToolStripSeparator(),
            this.miSave, this.miExport, new ToolStripSeparator(), miExit
        });

        var editMenu = new ToolStripMenuItem("&Edit");
        this.miUndo = new ToolStripMenuItem("&Undo", null, this.Undo_Click) { ShortcutKeys = Keys.Control | Keys.Z };
        this.miRedo = new ToolStripMenuItem("&Redo", null, this.Redo_Click) { ShortcutKeys = Keys.Control | Keys.Y };
        this.miCut = new ToolStripMenuItem("Cu&t", null, this.Cut_Click) { ShortcutKeys = Keys.Control | Keys.X };
        this.miCopy = new ToolStripMenuItem("&Copy", null, this.Copy_Click) { ShortcutKeys = Keys.Control | Keys.C };
        this.miPaste = new ToolStripMenuItem("&Paste", null, this.Paste_Click) { ShortcutKeys = Keys.Control | Keys.V };
        this.miDelete = new ToolStripMenuItem("&Delete Selection", null, this.Delete_Click) { ShortcutKeys = Keys.Delete };
        this.miSelectAll = new ToolStripMenuItem("Select &All", null, this.SelectAll_Click) { ShortcutKeys = Keys.Control | Keys.A };
        this.miTrim = new ToolStripMenuItem("T&rim to Selection", null, this.Trim_Click);
        this.miSilence = new ToolStripMenuItem("&Silence Selection", null, this.Silence_Click);
        editMenu.DropDownItems.AddRange(new ToolStripItem[]
        {
            this.miUndo, this.miRedo, new ToolStripSeparator(),
            this.miCut, this.miCopy, this.miPaste, this.miDelete, new ToolStripSeparator(),
            this.miSelectAll, this.miTrim, this.miSilence
        });

        var effectsMenu = new ToolStripMenuItem("E&ffects");
        this.miNormalize = new ToolStripMenuItem("&Normalize (peak)", null, this.Normalize_Click);
        this.miLoudness = new ToolStripMenuItem("Make &Louder (loudness normalize)…", null, this.LoudnessNormalize_Click);
        this.miAmplify = new ToolStripMenuItem("&Amplify…", null, this.Amplify_Click);
        this.miFadeIn = new ToolStripMenuItem("Fade &In", null, this.FadeIn_Click);
        this.miFadeOut = new ToolStripMenuItem("Fade &Out", null, this.FadeOut_Click);
        effectsMenu.DropDownItems.AddRange(new ToolStripItem[]
        {
            this.miNormalize, this.miLoudness, this.miAmplify, new ToolStripSeparator(),
            this.miFadeIn, this.miFadeOut
        });

        var trackMenu = new ToolStripMenuItem("&Track");
        var miAddTrack = new ToolStripMenuItem("&Add Track", null, this.AddTrack_Click) { ShortcutKeys = Keys.Control | Keys.T };
        var miImportTrack = new ToolStripMenuItem("&Import Audio as Track…", null, this.ImportTrack_Click);
        this.miRemoveTrack = new ToolStripMenuItem("&Remove Selected Track", null, this.RemoveTrack_Click);
        trackMenu.DropDownItems.AddRange(new ToolStripItem[]
        {
            miAddTrack, miImportTrack, new ToolStripSeparator(), this.miRemoveTrack
        });

        var helpMenu = new ToolStripMenuItem("&Help");
        var miShortcuts = new ToolStripMenuItem("&Keyboard Shortcuts", null, this.Shortcuts_Click) { ShortcutKeys = Keys.F1 };
        var miCheckForUpdates = new ToolStripMenuItem("Check for &Updates…", null, this.CheckForUpdates_Click);
        var miAbout = new ToolStripMenuItem("&About", null, this.About_Click);
        helpMenu.DropDownItems.AddRange(new ToolStripItem[] { miShortcuts, new ToolStripSeparator(), miCheckForUpdates, miAbout });

        this.menuStrip.Items.AddRange(new ToolStripItem[]
        {
            fileMenu, editMenu, effectsMenu, trackMenu, helpMenu
        });

        // --- transport panel ------------------------------------------------
        this.transportPanel.Dock = DockStyle.Top;
        this.transportPanel.Height = 56;

        this.recordButton.Location = new Point(10, 8);
        this.recordButton.Size = new Size(92, 40);
        this.recordButton.Font = new Font("Segoe UI", 10F, FontStyle.Bold);
        this.recordButton.ForeColor = Color.FromArgb(200, 40, 40);
        this.recordButton.Text = "● Record";
        this.recordButton.UseVisualStyleBackColor = true;
        this.recordButton.Click += this.recordButton_Click;

        this.stopButton.Location = new Point(108, 8);
        this.stopButton.Size = new Size(74, 40);
        this.stopButton.Font = new Font("Segoe UI", 10F);
        this.stopButton.Text = "■ Stop";
        this.stopButton.UseVisualStyleBackColor = true;
        this.stopButton.Click += this.stopButton_Click;

        this.playButton.Location = new Point(188, 8);
        this.playButton.Size = new Size(74, 40);
        this.playButton.Font = new Font("Segoe UI", 10F);
        this.playButton.Text = "▶ Play";
        this.playButton.UseVisualStyleBackColor = true;
        this.playButton.Click += this.playButton_Click;

        this.replayButton.Location = new Point(268, 8);
        this.replayButton.Size = new Size(84, 40);
        this.replayButton.Font = new Font("Segoe UI", 10F);
        this.replayButton.Text = "Replay";
        this.replayButton.UseVisualStyleBackColor = true;
        this.replayButton.Click += this.replayButton_Click;

        this.addTrackButton.Location = new Point(366, 8);
        this.addTrackButton.Size = new Size(100, 40);
        this.addTrackButton.Font = new Font("Segoe UI", 9.5F);
        this.addTrackButton.Text = "+ Add Track";
        this.addTrackButton.UseVisualStyleBackColor = true;
        this.addTrackButton.Click += this.AddTrack_Click;

        this.removeTrackButton.Location = new Point(472, 8);
        this.removeTrackButton.Size = new Size(118, 40);
        this.removeTrackButton.Font = new Font("Segoe UI", 9.5F);
        this.removeTrackButton.Text = "− Remove Track";
        this.removeTrackButton.UseVisualStyleBackColor = true;
        this.removeTrackButton.Click += this.RemoveTrack_Click;

        this.zoomOutButton.Location = new Point(608, 13);
        this.zoomOutButton.Size = new Size(36, 30);
        this.zoomOutButton.Font = new Font("Segoe UI", 11F, FontStyle.Bold);
        this.zoomOutButton.Text = "−";
        this.zoomOutButton.UseVisualStyleBackColor = true;
        this.zoomOutButton.Click += this.zoomOutButton_Click;

        this.zoomInButton.Location = new Point(648, 13);
        this.zoomInButton.Size = new Size(36, 30);
        this.zoomInButton.Font = new Font("Segoe UI", 11F, FontStyle.Bold);
        this.zoomInButton.Text = "+";
        this.zoomInButton.UseVisualStyleBackColor = true;
        this.zoomInButton.Click += this.zoomInButton_Click;

        this.zoomFitButton.Location = new Point(688, 13);
        this.zoomFitButton.Size = new Size(50, 30);
        this.zoomFitButton.Text = "Fit";
        this.zoomFitButton.UseVisualStyleBackColor = true;
        this.zoomFitButton.Click += this.zoomFitButton_Click;

        this.transportPanel.Controls.Add(this.recordButton);
        this.transportPanel.Controls.Add(this.stopButton);
        this.transportPanel.Controls.Add(this.playButton);
        this.transportPanel.Controls.Add(this.replayButton);
        this.transportPanel.Controls.Add(this.addTrackButton);
        this.transportPanel.Controls.Add(this.removeTrackButton);
        this.transportPanel.Controls.Add(this.zoomOutButton);
        this.transportPanel.Controls.Add(this.zoomInButton);
        this.transportPanel.Controls.Add(this.zoomFitButton);

        // --- device panel ---------------------------------------------------
        this.devicePanel.Dock = DockStyle.Top;
        this.devicePanel.Height = 56;

        this.micLabel.Location = new Point(10, 6);
        this.micLabel.Size = new Size(120, 15);
        this.micLabel.Text = "Microphone (input):";

        this.micCombo.Location = new Point(10, 24);
        this.micCombo.Size = new Size(250, 25);
        this.micCombo.DropDownStyle = ComboBoxStyle.DropDownList;
        this.micCombo.SelectedIndexChanged += this.micCombo_SelectedIndexChanged;

        this.speakerLabel.Location = new Point(276, 6);
        this.speakerLabel.Size = new Size(140, 15);
        this.speakerLabel.Text = "Speakers (output):";

        this.speakerCombo.Location = new Point(276, 24);
        this.speakerCombo.Size = new Size(250, 25);
        this.speakerCombo.DropDownStyle = ComboBoxStyle.DropDownList;

        this.refreshButton.Location = new Point(538, 23);
        this.refreshButton.Size = new Size(70, 26);
        this.refreshButton.Text = "Rescan";
        this.refreshButton.UseVisualStyleBackColor = true;
        this.refreshButton.Click += this.refreshButton_Click;

        this.levelLabel.Location = new Point(628, 6);
        this.levelLabel.Size = new Size(160, 15);
        this.levelLabel.Text = "Input level (live):";

        this.levelBar.Location = new Point(628, 25);
        this.levelBar.Size = new Size(250, 20);
        this.levelBar.Maximum = 100;

        this.devicePanel.Controls.Add(this.micLabel);
        this.devicePanel.Controls.Add(this.micCombo);
        this.devicePanel.Controls.Add(this.speakerLabel);
        this.devicePanel.Controls.Add(this.speakerCombo);
        this.devicePanel.Controls.Add(this.refreshButton);
        this.devicePanel.Controls.Add(this.levelLabel);
        this.devicePanel.Controls.Add(this.levelBar);

        // --- edit panel -----------------------------------------------------
        this.editPanel.Dock = DockStyle.Top;
        this.editPanel.Height = 44;

        this.undoButton.Location = new Point(10, 7);
        this.undoButton.Size = new Size(64, 30);
        this.undoButton.Text = "Undo";
        this.undoButton.UseVisualStyleBackColor = true;
        this.undoButton.Click += this.Undo_Click;

        this.redoButton.Location = new Point(78, 7);
        this.redoButton.Size = new Size(64, 30);
        this.redoButton.Text = "Redo";
        this.redoButton.UseVisualStyleBackColor = true;
        this.redoButton.Click += this.Redo_Click;

        this.cutButton.Location = new Point(154, 7);
        this.cutButton.Size = new Size(56, 30);
        this.cutButton.Text = "Cut";
        this.cutButton.UseVisualStyleBackColor = true;
        this.cutButton.Click += this.Cut_Click;

        this.copyButton.Location = new Point(214, 7);
        this.copyButton.Size = new Size(60, 30);
        this.copyButton.Text = "Copy";
        this.copyButton.UseVisualStyleBackColor = true;
        this.copyButton.Click += this.Copy_Click;

        this.pasteButton.Location = new Point(278, 7);
        this.pasteButton.Size = new Size(62, 30);
        this.pasteButton.Text = "Paste";
        this.pasteButton.UseVisualStyleBackColor = true;
        this.pasteButton.Click += this.Paste_Click;

        this.deleteButton.Location = new Point(344, 7);
        this.deleteButton.Size = new Size(66, 30);
        this.deleteButton.Text = "Delete";
        this.deleteButton.UseVisualStyleBackColor = true;
        this.deleteButton.Click += this.Delete_Click;

        this.trimButton.Location = new Point(422, 7);
        this.trimButton.Size = new Size(62, 30);
        this.trimButton.Text = "Trim";
        this.trimButton.UseVisualStyleBackColor = true;
        this.trimButton.Click += this.Trim_Click;

        this.silenceButton.Location = new Point(488, 7);
        this.silenceButton.Size = new Size(74, 30);
        this.silenceButton.Text = "Silence";
        this.silenceButton.UseVisualStyleBackColor = true;
        this.silenceButton.Click += this.Silence_Click;

        this.timeLabel.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        this.timeLabel.Location = new Point(880, 4);
        this.timeLabel.Size = new Size(200, 34);
        this.timeLabel.Font = new Font("Consolas", 15F, FontStyle.Bold);
        this.timeLabel.TextAlign = ContentAlignment.MiddleRight;
        this.timeLabel.Text = "0:00.000";

        this.editPanel.Controls.Add(this.undoButton);
        this.editPanel.Controls.Add(this.redoButton);
        this.editPanel.Controls.Add(this.cutButton);
        this.editPanel.Controls.Add(this.copyButton);
        this.editPanel.Controls.Add(this.pasteButton);
        this.editPanel.Controls.Add(this.deleteButton);
        this.editPanel.Controls.Add(this.trimButton);
        this.editPanel.Controls.Add(this.silenceButton);
        this.editPanel.Controls.Add(this.timeLabel);

        // --- waveform / status ----------------------------------------------
        this.waveform.Dock = DockStyle.Fill;

        this.statusLabel.Spring = true;
        this.statusLabel.TextAlign = ContentAlignment.MiddleLeft;
        this.statusStrip.Items.Add(this.statusLabel);
        this.statusStrip.SizingGrip = false;
        this.statusStrip.Dock = DockStyle.Bottom;

        // --- timers ---------------------------------------------------------
        this.levelTimer.Interval = 50;
        this.levelTimer.Tick += this.levelTimer_Tick;
        this.playTimer.Interval = 33;
        this.playTimer.Tick += this.playTimer_Tick;

        // --- form -----------------------------------------------------------
        this.AutoScaleMode = AutoScaleMode.Font;
        this.ClientSize = new Size(1090, 700);
        this.MinimumSize = new Size(1106, 600);
        this.Controls.Add(this.waveform);
        this.Controls.Add(this.statusStrip);
        this.Controls.Add(this.editPanel);
        this.Controls.Add(this.devicePanel);
        this.Controls.Add(this.transportPanel);
        this.Controls.Add(this.menuStrip);
        this.MainMenuStrip = this.menuStrip;
        this.Text = "SlinnerB's Music Studio";
        this.Load += this.MainForm_Load;
        this.FormClosing += this.MainForm_FormClosing;
    }
}
