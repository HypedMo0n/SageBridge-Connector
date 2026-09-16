using System;
using System.Threading.Tasks;
using System.Windows.Forms;
using SageBridge.Connector;

namespace SageBridge.GuiPanel
{
    internal static class FirstRunWindow
    {
        [STAThread]
        public static int Run(string workerUrl)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            var form = new Form
            {
                Text = "SageBridge Connector",
                StartPosition = FormStartPosition.CenterScreen,
                Width = 480,
                Height = 300,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false,
                MinimizeBox = false
            };

            var title = new Label
            {
                Text = "SageBridge Connector",
                Font = new System.Drawing.Font("Segoe UI", 14F, System.Drawing.FontStyle.Bold),
                AutoSize = true,
                Location = new System.Drawing.Point(24, 16)
            };
            var subtitle = new Label
            {
                Text = "Connect this computer to SageBridge",
                Font = new System.Drawing.Font("Segoe UI", 9F),
                AutoSize = true,
                Location = new System.Drawing.Point(24, 48)
            };
            var codeLabel = new Label
            {
                Text = "Pairing code:",
                AutoSize = true,
                Location = new System.Drawing.Point(24, 84)
            };
            var codeInput = new TextBox
            {
                Location = new System.Drawing.Point(24, 108),
                Width = 300,
                Font = new System.Drawing.Font("Consolas", 12F),
                MaxLength = 9
            };
            var status = new Label
            {
                Text = "Enter the pairing code shown in SageBridge.",
                AutoSize = true,
                Location = new System.Drawing.Point(24, 144),
                ForeColor = System.Drawing.Color.Gray
            };
            var connectButton = new Button
            {
                Text = "Connect",
                Location = new System.Drawing.Point(24, 180),
                Width = 120,
                Height = 36,
                Enabled = false
            };

            codeInput.TextChanged += (s, e) =>
            {
                connectButton.Enabled = codeInput.Text.Trim().Length >= 4;
            };

            connectButton.Click += async (s, e) =>
            {
                connectButton.Enabled = false;
                codeInput.Enabled = false;
                status.Text = "Connecting…";
                status.Refresh();
                try
                {
                    var client = new PairingClient(workerUrl);
                    var result = await client.ValidatePairingCodeAsync(codeInput.Text);
                    CredentialManager.StoreCredential(result.ConnectorId, result.Credential);
                    status.Text = "Connected successfully!";
                    status.Refresh();
                    await Task.Delay(1200);
                    form.DialogResult = DialogResult.OK;
                    form.Close();
                }
                catch (Exception ex)
                {
                    status.Text = $"Pairing failed: {ex.Message}";
                    connectButton.Enabled = true;
                    codeInput.Enabled = true;
                    codeInput.Focus();
                }
            };

            form.Controls.Add(title);
            form.Controls.Add(subtitle);
            form.Controls.Add(codeLabel);
            form.Controls.Add(codeInput);
            form.Controls.Add(status);
            form.Controls.Add(connectButton);

            form.Shown += (s, e) => codeInput.Focus();

            return form.ShowDialog() == DialogResult.OK ? 0 : 1;
        }
    }
}
