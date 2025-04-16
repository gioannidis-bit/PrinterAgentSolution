using PrinterAgent.Core;
using System;
using System.Collections.Generic;
using System.Windows.Forms;

namespace PrinterAgentUI
{
    public partial class Form1 : Form
    {
        public Form1()
        {
            InitializeComponent();
            LoadPrinters();
        }

        // Load installed printers into the list box.
        private void LoadPrinters()
        {
            List<string> printers = PrinterHelper.GetInstalledPrinters();
            listBoxPrinters.Items.Clear();
            listBoxPrinters.Items.AddRange(printers.ToArray());
        }

        // Button click event for sending a test print.
        private void btnTestPrint_Click(object sender, EventArgs e)
        {
            if (listBoxPrinters.SelectedItem == null)
            {
                MessageBox.Show("Please select a printer.");
                return;
            }

            string selectedPrinter = listBoxPrinters.SelectedItem.ToString();
            string documentContent = richTextBoxHtmlEditor.Text;

            bool result = PrinterHelper.SendTestPrint(selectedPrinter, documentContent);
            if (result)
            {
                lblStatus.Text = $"Test print sent to {selectedPrinter} successfully.";
            }
            else
            {
                lblStatus.Text = $"Failed to send test print to {selectedPrinter}.";
            }
        }

        // Optional: Refresh printer list (e.g., by a button click).
        private void btnRefresh_Click(object sender, EventArgs e)
        {
            LoadPrinters();
            lblStatus.Text = "Printer list refreshed.";
        }
    }
}
