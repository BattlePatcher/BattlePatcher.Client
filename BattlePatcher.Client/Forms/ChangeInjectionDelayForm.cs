using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace BattlePatcher.Client.Forms
{
    public partial class ChangeInjectionDelayForm : Form
    {
        public int InputDelay => (int)delayInput.Value;

        public ChangeInjectionDelayForm()
        {
            InitializeComponent();

            delayInput.Value = Program.Config.InjectionDelay;
        }

        private void confirmButton_Click(object sender, EventArgs e)
        {
            if (InputDelay < 0)
            {
                MessageBox.Show(
                    "The injection delay must not be a negative value.", "BattlePatcher",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);

                DialogResult = DialogResult.None;
            }
            else if (InputDelay < 500)
            {
                var warningResult = MessageBox.Show(
                    "Using an injection delay of less than 500 milliseconds is highly discouraged, this may " +
                    "lead to many game crashes. Are you sure you want to proceed?", "BattlePatcher",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning);

                if (warningResult == DialogResult.No)
                {
                    DialogResult = DialogResult.None;
                }
                else
                {
                    Close();
                }
            }
            else
            {
                Close();
            }
        }
    }
}
