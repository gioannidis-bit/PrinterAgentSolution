namespace PrinterAgentUI
{
    partial class Form1
    {
        private System.ComponentModel.IContainer components = null;
        private System.Windows.Forms.ListBox listBoxPrinters;
        private System.Windows.Forms.RichTextBox richTextBoxHtmlEditor;
        private System.Windows.Forms.Button btnTestPrint;
        private System.Windows.Forms.Button btnRefresh;
        private System.Windows.Forms.Label lblStatus;

        private void InitializeComponent()
        {
            this.listBoxPrinters = new System.Windows.Forms.ListBox();
            this.richTextBoxHtmlEditor = new System.Windows.Forms.RichTextBox();
            this.btnTestPrint = new System.Windows.Forms.Button();
            this.btnRefresh = new System.Windows.Forms.Button();
            this.lblStatus = new System.Windows.Forms.Label();
            this.SuspendLayout();
            // 
            // listBoxPrinters
            // 
            this.listBoxPrinters.FormattingEnabled = true;
            this.listBoxPrinters.Location = new System.Drawing.Point(16, 15);
            this.listBoxPrinters.Name = "listBoxPrinters";
            this.listBoxPrinters.Size = new System.Drawing.Size(363, 121);
            this.listBoxPrinters.TabIndex = 0;
            // 
            // richTextBoxHtmlEditor
            // 
            this.richTextBoxHtmlEditor.Location = new System.Drawing.Point(16, 160);
            this.richTextBoxHtmlEditor.Name = "richTextBoxHtmlEditor";
            this.richTextBoxHtmlEditor.Size = new System.Drawing.Size(832, 503);
            this.richTextBoxHtmlEditor.TabIndex = 1;
            this.richTextBoxHtmlEditor.Text = "<html>\n<body>\n<h1>Test Document</h1>\n<p>Your content here...</p>\n</body>\n</html>";
            // 
            // btnTestPrint
            // 
            this.btnTestPrint.Location = new System.Drawing.Point(698, 12);
            this.btnTestPrint.Name = "btnTestPrint";
            this.btnTestPrint.Size = new System.Drawing.Size(150, 30);
            this.btnTestPrint.TabIndex = 2;
            this.btnTestPrint.Text = "Send Test Print";
            this.btnTestPrint.UseVisualStyleBackColor = true;
            this.btnTestPrint.Click += new System.EventHandler(this.btnTestPrint_Click);
            // 
            // btnRefresh
            // 
            this.btnRefresh.Location = new System.Drawing.Point(698, 48);
            this.btnRefresh.Name = "btnRefresh";
            this.btnRefresh.Size = new System.Drawing.Size(150, 30);
            this.btnRefresh.TabIndex = 3;
            this.btnRefresh.Text = "Refresh Printers";
            this.btnRefresh.UseVisualStyleBackColor = true;
            this.btnRefresh.Click += new System.EventHandler(this.btnRefresh_Click);
            // 
            // lblStatus
            // 
            this.lblStatus.AutoSize = true;
            this.lblStatus.Location = new System.Drawing.Point(503, 12);
            this.lblStatus.Name = "lblStatus";
            this.lblStatus.Size = new System.Drawing.Size(37, 13);
            this.lblStatus.TabIndex = 4;
            this.lblStatus.Text = "Status";
            // 
            // Form1
            // 
            this.ClientSize = new System.Drawing.Size(945, 707);
            this.Controls.Add(this.lblStatus);
            this.Controls.Add(this.btnRefresh);
            this.Controls.Add(this.btnTestPrint);
            this.Controls.Add(this.richTextBoxHtmlEditor);
            this.Controls.Add(this.listBoxPrinters);
            this.Name = "Form1";
            this.Text = "Printer Agent UI Tester";
            this.ResumeLayout(false);
            this.PerformLayout();

        }
    }
}
