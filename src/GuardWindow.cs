using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Security.Cryptography;
using System.Windows.Forms;

[assembly: AssemblyTitle("Steam Guard для Windows")]
[assembly: AssemblyDescription("Неофициальный клиент Steam Guard для Windows")]
[assembly: AssemblyVersion("0.1.0.0")]

namespace LocalSteamGuard
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;

            string profile = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            try
            {
                using (var vault = new Vault(Path.Combine(profile, "LocalSteamGuard")))
                using (var steam = new SteamApi())
                using (var window = new GuardWindow(vault, vault.Load(), steam))
                    Application.Run(window);
            }
            catch (Exception error)
            {
                if (!(error is IOException || error is UnauthorizedAccessException
                    || error is InvalidOperationException || error is InvalidDataException
                    || error is CryptographicException))
                    throw;

                string message = error is InvalidOperationException || error is InvalidDataException
                    ? error.Message
                    : "Не удалось открыть локальное хранилище Steam Guard.";
                MessageBox.Show(message, "Steam Guard", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }

    internal sealed class GuardWindow : Form
    {
        private readonly Vault vault;
        private readonly SteamApi steam;
        private List<Account> accounts;
        private readonly TextBox search;
        private readonly ListBox accountList;
        private readonly Label listMessage;
        private readonly TableLayoutPanel codePanel;
        private readonly TableLayoutPanel emptyPanel;
        private readonly Label emptyTitle;
        private readonly Label emptyHint;
        private readonly Label accountName;
        private readonly Label codeText;
        private readonly Label timeText;
        private readonly Label status;
        private readonly Button copy;
        private readonly Button requests;
        private readonly Button recovery;
        private readonly TimeBar progress;
        private readonly Timer timer;
        private Account selected;
        private long shownPeriod = -1;
        private bool refreshingList;

        public GuardWindow(Vault vault, List<Account> accounts, SteamApi steam)
        {
            this.vault = vault;
            this.steam = steam;
            this.accounts = accounts;
            Text = "Steam Guard для Windows";
            ShowIcon = false;
            StartPosition = FormStartPosition.CenterScreen;
            AutoScaleDimensions = new SizeF(96, 96);
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(930, 556);
            MinimumSize = new Size(860, 530);
            BackColor = Theme.Background;
            ForeColor = Theme.Text;
            Font = Theme.Body;

            var root = Theme.Rows(60, 0, 27);
            root.Padding = new Padding(26, 22, 26, 14);
            Controls.Add(root);

            var header = new TableLayoutPanel { Dock = DockStyle.Fill, Margin = Padding.Empty, ColumnCount = 2 };
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 365));
            var title = Theme.Label("Steam Guard", Theme.Heading, Theme.Text);
            title.TextAlign = ContentAlignment.TopLeft;
            header.Controls.Add(title, 0, 0);
            var topActions = Theme.ButtonRow(true);
            topActions.Padding = new Padding(0, 5, 0, 0);
            var add = Theme.Button("Подключить аккаунт", 177, true);
            add.Margin = new Padding(0, 0, 0, 0);
            add.Click += delegate { ConnectAccount(); };
            var import = Theme.Button("Другие способы", 145);
            var other = new ContextMenuStrip();
            other.Items.Add("Импорт файлов .maFile", null, delegate { ImportAccounts(); });
            other.Items.Add("Ввести shared_secret", null, delegate { AddAccount(); });
            import.Click += delegate { other.Show(import, new Point(0, import.Height)); };
            Disposed += delegate { other.Dispose(); };
            topActions.Controls.Add(add);
            topActions.Controls.Add(import);
            header.Controls.Add(topActions, 1, 0);
            root.Controls.Add(header, 0, 0);

            var columns = new TableLayoutPanel
                { Dock = DockStyle.Fill, Margin = Padding.Empty, ColumnCount = 3 };
            columns.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 271));
            columns.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 16));
            columns.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.Controls.Add(columns, 0, 1);

            var left = Theme.Rows(31, 23, 33, 0);
            left.Padding = new Padding(17);
            left.BackColor = Theme.Surface;
            left.Controls.Add(Theme.Label("Аккаунты", Theme.Section, Theme.Text), 0, 0);
            left.Controls.Add(Theme.Label("Поиск по имени", Theme.Body, Theme.Muted), 0, 1);
            search = Theme.FieldBox("Поиск по имени аккаунта", Account.NameLimit);
            search.TextChanged += delegate { RefreshAccounts(selected); };
            left.Controls.Add(search, 0, 2);
            var listArea = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty };
            accountList = new ListBox
            {
                AccessibleName = "Аккаунты Steam", Dock = DockStyle.Fill, BorderStyle = BorderStyle.None,
                DrawMode = DrawMode.OwnerDrawFixed, ItemHeight = 37, IntegralHeight = false,
                BackColor = Theme.Surface, ForeColor = Theme.Text, Font = Theme.Body
            };
            accountList.DrawItem += DrawAccount;
            accountList.SelectedIndexChanged += delegate { if (!refreshingList) SelectAccount(); };
            listArea.Controls.Add(accountList);
            listMessage = Theme.Label("", Theme.Body, Theme.Muted);
            listArea.Controls.Add(listMessage);
            left.Controls.Add(listArea, 0, 3);
            columns.Controls.Add(left, 0, 0);

            var detail = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty, BackColor = Theme.Surface };
            codePanel = Theme.Rows(23, 43, 88, 13, 34, 46, 0, 38);
            codePanel.Padding = new Padding(26, 20, 26, 14);
            codePanel.Controls.Add(Theme.Label("Код для входа", Theme.Body, Theme.Muted), 0, 0);
            accountName = Theme.Label("", Theme.Account, Theme.Text);
            codePanel.Controls.Add(accountName, 0, 1);
            codeText = Theme.Label("", Theme.Code, Theme.Accent);
            codeText.AccessibleName = "Код Steam Guard";
            codePanel.Controls.Add(codeText, 0, 2);
            progress = new TimeBar { Dock = DockStyle.Fill, Margin = new Padding(0, 5, 0, 3) };
            codePanel.Controls.Add(progress, 0, 3);
            timeText = Theme.Label("", Theme.Body, Theme.Muted);
            codePanel.Controls.Add(timeText, 0, 4);
            var copyRow = Theme.ButtonRow(false);
            copy = Theme.Button("Копировать код", 150, true);
            copy.Click += delegate { CopyCode(); };
            copyRow.Controls.Add(copy);
            requests = Theme.Button("Подтверждения", 145);
            requests.Click += delegate
            {
                using (var dialog = new RequestsDialog(steam, selected))
                    dialog.ShowDialog(this);
            };
            copyRow.Controls.Add(requests);
            codePanel.Controls.Add(copyRow, 0, 5);

            var accountActions = Theme.ButtonRow(false);
            var rename = Theme.Button("Переименовать", 145);
            rename.Click += delegate { RenameAccount(); };
            var remove = Theme.Button("Удалить", 90);
            remove.Click += delegate { RemoveAccount(); };
            accountActions.Controls.Add(rename);
            recovery = Theme.Button("Код восстановления", 165);
            recovery.Click += delegate
            {
                using (var dialog = new RecoveryDialog(selected))
                    dialog.ShowDialog(this);
            };
            accountActions.Controls.Add(recovery);
            accountActions.Controls.Add(remove);
            codePanel.Controls.Add(accountActions, 0, 7);
            detail.Controls.Add(codePanel);

            emptyPanel = Theme.Rows(0, 38, 30, 0);
            emptyPanel.RowStyles[0].Height = 50;
            emptyPanel.RowStyles[3].Height = 50;
            emptyPanel.Padding = new Padding(18, 0, 18, 0);
            emptyTitle = Theme.Label("", Theme.Account, Theme.Text);
            emptyTitle.TextAlign = ContentAlignment.MiddleCenter;
            emptyHint = Theme.Label("", Theme.Body, Theme.Muted);
            emptyHint.TextAlign = ContentAlignment.MiddleCenter;
            emptyPanel.Controls.Add(emptyTitle, 0, 1);
            emptyPanel.Controls.Add(emptyHint, 0, 2);
            detail.Controls.Add(emptyPanel);
            columns.Controls.Add(detail, 2, 0);

            status = Theme.Label("Секреты зашифрованы в профиле Windows", Theme.Body, Theme.Muted);
            status.TextAlign = ContentAlignment.BottomLeft;
            root.Controls.Add(status, 0, 2);

            timer = new Timer { Interval = 200 };
            timer.Tick += delegate { UpdateCode(); };
            RefreshAccounts(null);
            timer.Start();
        }

        protected override bool ProcessCmdKey(ref Message message, Keys keyData)
        {
            if (keyData == (Keys.Control | Keys.F))
            {
                search.Focus();
                search.SelectAll();
                return true;
            }

            if (keyData == (Keys.Control | Keys.C) && !search.Focused && selected != null)
            {
                CopyCode();
                return true;
            }

            return base.ProcessCmdKey(ref message, keyData);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !IsDisposed)
            {
                if (timer != null)
                    timer.Dispose();
                foreach (Account account in accounts)
                    Forget(account);
            }
            base.Dispose(disposing);
        }

        private void DrawAccount(object sender, DrawItemEventArgs e)
        {
            if (e.Index < 0)
                return;

            var account = (Account)accountList.Items[e.Index];
            bool active = (e.State & DrawItemState.Selected) != 0;
            var row = new Rectangle(e.Bounds.X, e.Bounds.Y, e.Bounds.Width, e.Bounds.Height - 3);
            using (var fill = new SolidBrush(active ? Theme.Selected : Theme.Field))
                e.Graphics.FillRectangle(fill, row);

            if (active)
            {
                using (var accent = new SolidBrush(Theme.Accent))
                    e.Graphics.FillRectangle(accent, row.X, row.Y, 3, row.Height);
            }

            row.Inflate(-11, 0);
            TextRenderer.DrawText(e.Graphics, account.Name, Theme.Body, row, Theme.Text,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis
                | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
            e.DrawFocusRectangle();
        }

        private void RefreshAccounts(Account preferred)
        {
            string filter = search.Text.Trim();
            var matches = accounts
                .Where(account => account.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
                .OrderBy(account => account.Name, StringComparer.CurrentCultureIgnoreCase);

            refreshingList = true;
            accountList.BeginUpdate();
            accountList.Items.Clear();
            foreach (Account account in matches)
                accountList.Items.Add(account);

            if (accountList.Items.Count > 0)
            {
                int index = preferred == null ? -1 : accountList.Items.IndexOf(preferred);
                accountList.SelectedIndex = Math.Max(index, 0);
            }
            accountList.EndUpdate();
            refreshingList = false;

            bool hasMatch = accountList.Items.Count > 0;
            accountList.Visible = hasMatch;
            listMessage.Text = accounts.Count == 0 ? "Список пока пуст" : "Совпадений нет";
            listMessage.Visible = !hasMatch;
            SelectAccount();
        }

        private void SelectAccount()
        {
            selected = accountList.SelectedItem as Account;
            codePanel.Visible = selected != null;
            emptyPanel.Visible = selected == null;
            if (selected == null)
            {
                emptyTitle.Text = accounts.Count == 0 ? "Добавьте первый аккаунт" : "Аккаунты не найдены";
                emptyHint.Text = accounts.Count == 0
                    ? "Подключите свой аккаунт через Steam Mobile и SMS."
                    : "Измените запрос в поле поиска.";
                codeText.Text = "";
                return;
            }

            requests.Visible = selected.Registration != null;
            recovery.Visible = selected.Registration != null && !string.IsNullOrEmpty(selected.Registration.RecoveryCode);
            accountName.Text = selected.Name;
            shownPeriod = -1;
            copy.Text = "Копировать код";
            UpdateCode();
        }

        private string UpdateCode()
        {
            if (selected == null)
                return null;

            long now = GuardCode.CurrentUnixSeconds();
            long period = now / GuardCode.Period;
            if (period != shownPeriod)
            {
                codeText.Text = GuardCode.Generate(selected.Secret, now);
                copy.Text = "Копировать код";
                shownPeriod = period;
            }

            int remaining = GuardCode.Period - (int)(now % GuardCode.Period);
            if (progress.Seconds != remaining)
            {
                progress.Seconds = remaining;
                timeText.Text = "До следующего кода " + remaining.ToString(CultureInfo.InvariantCulture) + " с";
            }
            return codeText.Text;
        }

        private void CopyCode()
        {
            string code = UpdateCode();
            if (code == null)
                return;

            try
            {
                Clipboard.SetText(code);
                copy.Text = "Скопировано";
            }
            catch (ExternalException)
            {
                ShowMessage("Буфер обмена занят. Попробуйте ещё раз.", MessageBoxIcon.Warning);
            }
        }

        private void ConnectAccount()
        {
            using (var dialog = new ConnectionDialog(steam,
                id => accounts.Any(a => a.Registration != null && a.Registration.Id == id),
                () => vault.Save(accounts), StoreTransferredAccount))
            {
                dialog.ShowDialog(this);
                if (dialog.Connected != null)
                {
                    search.Clear();
                    RefreshAccounts(dialog.Connected);
                    status.Text = "Steam Guard перенесён на этот ПК";
                    accountList.Focus();
                }
            }
        }

        private Account StoreTransferredAccount(Account transferred)
        {
            string label = UniqueName(transferred.Name, accounts);
            var account = new Account(label, transferred.Secret, transferred.Registration);
            var updated = new List<Account>(accounts) { account };
            vault.Save(updated);
            accounts = updated;
            return account;
        }

        private void AddAccount()
        {
            using (var dialog = new AccountDialog(null))
            {
                if (dialog.ShowDialog(this) != DialogResult.OK)
                    return;

                var added = new Account(UniqueName(dialog.AccountName, accounts), dialog.TakeSecret());
                if (ContainsSecret(accounts, added))
                {
                    Forget(added);
                    ShowMessage("Этот секрет уже добавлен.", MessageBoxIcon.Information);
                    return;
                }

                var updated = new List<Account>(accounts) { added };
                if (Save(updated, added, "Аккаунт добавлен", true))
                    accountList.Focus();
                else
                    Forget(added);
            }
        }

        private void ImportAccounts()
        {
            using (var dialog = new OpenFileDialog
            {
                Title = "Импорт аккаунтов Steam Guard", Filter = "Файлы Steam Guard (*.maFile)|*.maFile",
                Multiselect = true, CheckFileExists = true, RestoreDirectory = true
            })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK)
                    return;
                ImportFiles(dialog.FileNames);
            }
        }

        private void ImportFiles(string[] paths)
        {
            var updated = new List<Account>(accounts);
            var errors = new List<string>();
            Account firstAdded = null;
            int duplicates = 0;
            foreach (string path in paths)
            {
                Account imported;
                try
                {
                    imported = AccountImport.Read(path);
                }
                catch (InvalidDataException error)
                {
                    errors.Add(Path.GetFileName(path) + " — " + error.Message);
                    continue;
                }
                catch (Exception error)
                {
                    if (!(error is IOException || error is UnauthorizedAccessException))
                        throw;
                    errors.Add(Path.GetFileName(path) + " — не удалось прочитать файл.");
                    continue;
                }

                if (ContainsSecret(updated, imported))
                {
                    duplicates++;
                    Forget(imported);
                    continue;
                }

                var added = new Account(UniqueName(imported.Name, updated), imported.Secret);
                updated.Add(added);
                if (firstAdded == null)
                    firstAdded = added;
            }

            string result = "Импортировано " + (updated.Count - accounts.Count)
                + "  ·  Повторов " + duplicates + "  ·  Ошибок " + errors.Count;
            if (firstAdded == null)
                status.Text = result;
            else if (!Save(updated, firstAdded, result, true))
            {
                foreach (Account added in updated.Skip(accounts.Count))
                    Forget(added);
            }
            else
                accountList.Focus();

            if (errors.Count != 0)
            {
                string details = string.Join(Environment.NewLine, errors.Take(8));
                if (errors.Count > 8)
                    details += Environment.NewLine + "Других файлов с ошибками " + (errors.Count - 8);
                ShowMessage(details, MessageBoxIcon.Warning);
            }
        }

        private void RenameAccount()
        {
            Account current = selected;
            if (current == null)
                return;

            using (var dialog = new AccountDialog(current.Name))
            {
                if (dialog.ShowDialog(this) != DialogResult.OK || dialog.AccountName == current.Name)
                    return;

                if (accounts.Any(account => account != current
                    && string.Equals(account.Name, dialog.AccountName, StringComparison.OrdinalIgnoreCase)))
                {
                    ShowMessage("Такое имя уже есть в списке.", MessageBoxIcon.Information);
                    return;
                }

                var renamed = new Account(dialog.AccountName, current.Secret, current.Registration);
                var updated = accounts.Select(account => account == current ? renamed : account).ToList();
                Save(updated, renamed, "Имя аккаунта обновлено", true);
            }
        }

        private void RemoveAccount()
        {
            Account current = selected;
            if (current == null)
                return;

            string prompt = "Удалить «" + current.Name + "» из списка на этом ПК?";
            if (current.Registration != null)
                prompt += Environment.NewLine + "Доступ к кодам на этом ПК будет потерян. Сначала перенеси Steam Guard на другое устройство либо сохрани код восстановления.";
            if (MessageBox.Show(this, prompt, Text, MessageBoxButtons.YesNo,
                MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                return;

            var updated = accounts.Where(account => account != current).ToList();
            if (Save(updated, null, "Аккаунт удалён из списка", false))
                Forget(current);
        }

        private bool Save(List<Account> updated, Account preferred, string message, bool resetSearch)
        {
            try
            {
                vault.Save(updated);
            }
            catch (Exception error)
            {
                if (!(error is IOException || error is UnauthorizedAccessException
                    || error is CryptographicException || error is SerializationException
                    || error is InvalidDataException))
                    throw;
                ShowMessage("Не удалось сохранить изменения в профиле Windows.", MessageBoxIcon.Error);
                return false;
            }

            accounts = updated;
            if (resetSearch)
                search.Clear();
            RefreshAccounts(preferred);
            status.Text = message;
            return true;
        }

        private static bool ContainsSecret(IEnumerable<Account> existing, Account candidate)
        {
            return existing.Any(account => account.Secret.SequenceEqual(candidate.Secret));
        }

        private static string UniqueName(string name, List<Account> existing)
        {
            string candidate = name;
            int copyNumber = 2;
            while (existing.Any(account => string.Equals(account.Name, candidate, StringComparison.OrdinalIgnoreCase)))
            {
                string suffix = " (" + copyNumber++ + ")";
                int length = Math.Min(name.Length, Account.NameLimit - suffix.Length);
                if (char.IsHighSurrogate(name[length - 1]))
                    length--;
                candidate = name.Substring(0, length).TrimEnd() + suffix;
            }
            return candidate;
        }

        private static void Forget(Account account)
        {
            Array.Clear(account.Secret, 0, account.Secret.Length);
            if (account.Registration != null)
                Array.Clear(account.Registration.IdentitySecret, 0, account.Registration.IdentitySecret.Length);
        }

        private void ShowMessage(string message, MessageBoxIcon icon)
        {
            MessageBox.Show(this, message, Text, MessageBoxButtons.OK, icon);
        }
    }

    internal sealed class AccountDialog : Form
    {
        private readonly TextBox name;
        private readonly TextBox secret;
        private readonly Label validation;
        private byte[] parsedSecret;

        public string AccountName { get; private set; }

        public AccountDialog(string currentName)
        {
            bool adding = currentName == null;
            Text = adding ? "Добавить аккаунт" : "Переименовать аккаунт";
            Font = Theme.Body;
            BackColor = Theme.Surface;
            ForeColor = Theme.Text;
            AutoScaleDimensions = new SizeF(96, 96);
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(480, adding ? 250 : 170);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            MinimizeBox = false;
            MaximizeBox = false;

            var layout = adding ? Theme.Rows(26, 34, 26, 34, 0, 38) : Theme.Rows(26, 34, 0, 38);
            layout.Padding = new Padding(20);
            Controls.Add(layout);
            layout.Controls.Add(Theme.Label("Имя аккаунта в списке", Theme.Body, Theme.Text), 0, 0);
            name = Theme.FieldBox("Имя аккаунта в списке", Account.NameLimit);
            name.Text = currentName ?? "";
            layout.Controls.Add(name, 0, 1);

            if (adding)
            {
                layout.Controls.Add(Theme.Label("shared_secret (Base64)", Theme.Body, Theme.Text), 0, 2);
                secret = Theme.FieldBox("Секрет Steam Guard в Base64", 256);
                secret.UseSystemPasswordChar = true;
                layout.Controls.Add(secret, 0, 3);
            }

            validation = Theme.Label("", Theme.Body, Color.FromArgb(255, 178, 153));
            layout.Controls.Add(validation, 0, adding ? 4 : 2);

            var buttons = Theme.ButtonRow(true);
            var save = Theme.Button(adding ? "Добавить" : "Сохранить", 110, true);
            save.Margin = Padding.Empty;
            save.Click += delegate { Submit(); };
            var cancel = Theme.Button("Отмена", 95);
            cancel.DialogResult = DialogResult.Cancel;
            buttons.Controls.Add(save);
            buttons.Controls.Add(cancel);
            layout.Controls.Add(buttons, 0, adding ? 5 : 3);
            AcceptButton = save;
            CancelButton = cancel;
            ActiveControl = name;
        }

        private void Submit()
        {
            string candidate = name.Text.Trim();
            if (!Account.IsValidName(candidate))
            {
                validation.Text = "Введите имя аккаунта.";
                name.Focus();
                return;
            }

            if (secret != null)
            {
                try
                {
                    parsedSecret = GuardCode.ReadSecret(secret.Text);
                }
                catch (FormatException)
                {
                    validation.Text = "Проверьте shared_secret в формате Base64.";
                    secret.Focus();
                    return;
                }
                secret.Clear();
            }

            AccountName = candidate;
            DialogResult = DialogResult.OK;
        }

        public byte[] TakeSecret()
        {
            byte[] result = parsedSecret;
            parsedSecret = null;
            return result;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && parsedSecret != null)
                Array.Clear(parsedSecret, 0, parsedSecret.Length);
            base.Dispose(disposing);
        }
    }
}
