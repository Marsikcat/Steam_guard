using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace LocalSteamGuard
{
    internal static class SteamUi
    {
        public static string Error(Exception error)
        {
            var steam = error as SteamApiError;
            if (steam != null)
            {
                switch (steam.Result)
                {
                    case 5: return "Steam не принял логин или пароль.";
                    case 65: case 88: return "Steam не принял проверочный код.";
                    case 84: return "Слишком много запросов. Попробуй позже.";
                    case 15: return "Steam отклонил доступ. Проверь активный Steam Guard и номер телефона.";
                    default: return steam.Message;
                }
            }
            if (error is TimeoutException || error is OperationCanceledException)
                return "Время ожидания истекло.";
            if (error is HttpRequestException)
                return "Сейчас не удалось связаться со Steam. Проверь подключение.";
            if (error is InvalidDataException || error is SerializationException || error is EndOfStreamException)
                return "Steam вернул неполный или неожиданный ответ.";
            if (error is IOException || error is UnauthorizedAccessException || error is CryptographicException)
                return "Не удалось записать данные в защищённое хранилище Windows.";
            return "Операцию не удалось завершить.";
        }

        public static void StyleDialog(Form dialog, string title, int width, int height)
        {
            dialog.Text = title;
            dialog.Font = Theme.Body;
            dialog.BackColor = Theme.Surface;
            dialog.ForeColor = Theme.Text;
            dialog.AutoScaleDimensions = new SizeF(96, 96);
            dialog.AutoScaleMode = AutoScaleMode.Dpi;
            dialog.ClientSize = new Size(width, height);
            dialog.FormBorderStyle = FormBorderStyle.FixedDialog;
            dialog.StartPosition = FormStartPosition.CenterParent;
            dialog.ShowInTaskbar = false;
            dialog.MinimizeBox = false;
            dialog.MaximizeBox = false;
        }
    }

    internal sealed class ConnectionDialog : Form
    {
        private enum Step { Login, ConfirmLogin, SendSms, EnterSms, Save, Done, Uncertain }
        private Step step;
        private readonly SteamApi api;
        private readonly Func<ulong, bool> isPresent;
        private readonly Action checkVault;
        private readonly Func<Account, Account> save;
        private readonly CancellationTokenSource stop = new CancellationTokenSource();
        private readonly Label heading;
        private readonly Label instruction;
        private readonly Label firstLabel;
        private readonly Label secondLabel;
        private readonly TextBox first;
        private readonly TextBox second;
        private readonly Button next;
        private readonly TableLayoutPanel columns;
        private readonly TableLayoutPanel loginFields;
        private readonly Panel separator;
        private readonly QrSignInPanel qrPanel;
        private readonly Button close;
        private readonly Label feedback;
        private SteamLogin login;
        private Account pending;
        private bool busy;
        private bool protectCommit;

        public Account Connected { get; private set; }

        public ConnectionDialog(SteamApi api, Func<ulong, bool> isPresent, Action checkVault, Func<Account, Account> save)
        {
            this.api = api;
            this.isPresent = isPresent;
            this.checkVault = checkVault;
            this.save = save;
            SteamUi.StyleDialog(this, "Подключить аккаунт Steam", 856, 511);
            columns = new TableLayoutPanel
            {
                ColumnCount = 3, RowCount = 1, Dock = DockStyle.Fill,
                Padding = new Padding(22, 16, 22, 16)
            };
            columns.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 52));
            columns.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 1));
            columns.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 48));
            columns.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            Controls.Add(columns);

            loginFields = Theme.Rows(43, 63, 26, 35, 26, 35, 0, 41);
            columns.Controls.Add(loginFields, 0, 0);
            var layout = loginFields;
            separator = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Field, Margin = Padding.Empty };
            columns.Controls.Add(separator, 1, 0);
            qrPanel = new QrSignInPanel(api);
            qrPanel.SignedIn += CompleteQrSignIn;
            columns.Controls.Add(qrPanel, 2, 0);
            Shown += delegate { StartQr(); };

            heading = Theme.Label("", Theme.Account, Theme.Text);
            instruction = Theme.Label("", Theme.Body, Theme.Muted);
            instruction.AutoEllipsis = false;
            firstLabel = Theme.Label("", Theme.Body, Theme.Text);
            secondLabel = Theme.Label("", Theme.Body, Theme.Text);
            first = Theme.FieldBox("Логин или код", 80);
            second = Theme.FieldBox("Пароль аккаунта Steam", 128);
            feedback = Theme.Label("", Theme.Body, Color.FromArgb(255, 192, 161));
            feedback.AutoEllipsis = false;
            layout.Controls.Add(heading, 0, 0);
            layout.Controls.Add(instruction, 0, 1);
            layout.Controls.Add(firstLabel, 0, 2);
            layout.Controls.Add(first, 0, 3);
            layout.Controls.Add(secondLabel, 0, 4);
            layout.Controls.Add(second, 0, 5);
            layout.Controls.Add(feedback, 0, 6);

            var buttons = Theme.ButtonRow(true);
            next = Theme.Button("", 182, true);
            next.Margin = Padding.Empty;
            next.Click += async delegate { await Advance(); };
            close = Theme.Button("Отмена", 98);
            close.Click += delegate { Close(); };
            buttons.Controls.Add(next);
            buttons.Controls.Add(close);
            layout.Controls.Add(buttons, 0, 7);
            AcceptButton = next;
            CancelButton = close;
            Display(Step.Login);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (protectCommit)
                e.Cancel = true;
            else if (step == Step.Save && pending != null && Connected == null)
            {
                e.Cancel = MessageBox.Show(this,
                    "Steam уже перенёс аутентификатор. Сохрани код восстановления перед выходом. Закрыть окно без сохранения аккаунта?",
                    Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes;
            }
            if (!e.Cancel)
            {
                qrPanel.Stop();
                stop.Cancel();
            }
            base.OnFormClosing(e);
        }

        private void Display(Step value)
        {
            step = value;
            feedback.Text = "";
            first.Clear();
            second.Clear();
            first.ReadOnly = false;
            second.UseSystemPasswordChar = true;
            string title;
            string text;
            string button;
            string label = "";
            switch (value)
            {
                case Step.Login:
                    title = "Вход в Steam";
                    text = "Введи логин и пароль аккаунта с действующим Steam Guard.";
                    button = "Войти";
                    label = "Логин Steam";
                    break;
                case Step.ConfirmLogin:
                    title = "Подтверждение входа";
                    bool mobile = login.Confirmations.Contains(4);
                    bool email = login.Confirmations.Contains(5);
                    text = mobile ? "Подтверди вход на телефоне в Steam Mobile и нажми «Проверить»."
                        : email ? "Подтверди вход по ссылке из письма Steam и нажми «Проверить»."
                        : "Введи проверочный код, который запрашивает Steam.";
                    button = "Проверить";
                    if (login.Confirmations.Contains(3))
                        label = mobile ? "Код Steam Guard, если используешь код" : "Код из Steam Mobile";
                    else if (login.Confirmations.Contains(2))
                        label = "Код из письма Steam";
                    break;
                case Step.SendSms:
                    title = "Перенос на этот ПК";
                    text = "Аккаунт " + login.Name + ". Steam отправит SMS на его номер. После переноса телефон перестанет выдавать коды для него, а обмены и Торговая площадка будут ограничены на 2 дня.";
                    button = "Отправить SMS";
                    break;
                case Step.EnterSms:
                    title = "Подтверждение переноса";
                    text = "Введи SMS-код от Steam. По кнопке «Перенести» текущий аутентификатор этого аккаунта будет заменён.";
                    button = "Перенести";
                    label = "Код из SMS";
                    break;
                case Step.Save:
                    title = "Сохранение аккаунта";
                    text = string.IsNullOrEmpty(pending.Registration.RecoveryCode)
                        ? "Steam перенёс аутентификатор. Повтори попытку сохранить аккаунт."
                        : "Перенос выполнен. Запиши новый код восстановления и сохрани аккаунт на этом ПК.";
                    button = "Сохранить";
                    label = "Код восстановления";
                    first.ReadOnly = true;
                    first.Text = pending.Registration.RecoveryCode ?? "";
                    break;
                case Step.Done:
                    title = "Аккаунт подключён";
                    text = string.IsNullOrEmpty(Connected.Registration.RecoveryCode)
                        ? "Аккаунт сохранён. Код восстановления отсутствовал в ответе Steam."
                        : "Запиши новый код восстановления. Он поможет вернуть доступ через Steam, если хранилище Windows станет недоступно.";
                    button = "Готово";
                    label = "Код восстановления";
                    first.ReadOnly = true;
                    first.Text = Connected.Registration.RecoveryCode ?? "";
                    break;
                default:
                    title = "Проверь Steam Guard";
                    text = "Результат переноса неизвестен. Проверь, выдаёт ли телефон коды для этого аккаунта. Если доступ потерян, используй восстановление Steam.";
                    button = "Закрыть";
                    break;
            }
            heading.Text = title;
            instruction.Text = text;
            next.Text = button;
            bool choosingLogin = value == Step.Login;
            columns.SuspendLayout();
            qrPanel.Visible = separator.Visible = choosingLogin;
            loginFields.Padding = choosingLogin ? new Padding(0, 0, 22, 0) : new Padding(25, 0, 25, 0);
            columns.SetColumnSpan(loginFields, choosingLogin ? 1 : 3);
            columns.ResumeLayout(true);
            if (!choosingLogin)
                qrPanel.Stop();
            close.Visible = value != Step.Done && value != Step.Uncertain;
            first.Visible = firstLabel.Visible = label.Length > 0;
            firstLabel.Text = label;
            second.Visible = secondLabel.Visible = value == Step.Login;
            secondLabel.Text = "Пароль";
            if (first.Visible)
                first.Focus();
            else
                next.Focus();
        }

        private async void StartQr()
        {
            if (step == Step.Login && !IsDisposed)
                await qrPanel.LoadCode();
        }

        private void CompleteQrSignIn(SteamLogin session)
        {
            if (step != Step.Login || busy)
                return;
            if (isPresent(session.SteamId))
            {
                qrPanel.Stop("Этот аккаунт уже подключён на этом ПК.");
                return;
            }
            login = session;
            Display(Step.SendSms);
        }

        private async Task Advance()
        {
            if (busy)
                return;
            if (step == Step.Done || step == Step.Uncertain)
            {
                DialogResult = step == Step.Done ? DialogResult.OK : DialogResult.Cancel;
                Close();
                return;
            }

            Step started = step;
            string value = first.Text.Trim();
            if ((step == Step.Login && (value.Length == 0 || second.TextLength == 0))
                || (step == Step.EnterSms && (value.Length == 0 || value.Length > 15 || !value.All(char.IsLetterOrDigit))))
            {
                feedback.Text = step == Step.Login ? "Введи логин и пароль Steam." : "Введи код из SMS от Steam.";
                return;
            }
            if (step == Step.ConfirmLogin && first.Visible && value.Length == 0
                && !login.Confirmations.Contains(4) && !login.Confirmations.Contains(5))
            {
                feedback.Text = "Введи проверочный код.";
                return;
            }

            busy = true;
            next.Enabled = first.Enabled = second.Enabled = false;
            if (started == Step.Login)
            {
                qrPanel.Enabled = false;
                qrPanel.Stop("Выполняется вход по паролю…");
            }
            protectCommit = step == Step.EnterSms || step == Step.Save;
            close.Enabled = !protectCommit;
            feedback.Text = step == Step.ConfirmLogin ? "Ожидаю подтверждение от Steam…" : "Выполняю запрос…";
            try
            {
                var cancel = stop.Token;
                switch (started)
                {
                    case Step.Login:
                        byte[] password = Encoding.UTF8.GetBytes(second.Text);
                        second.Clear();
                        try { login = await api.BeginLogin(value, password, cancel); }
                        finally { Array.Clear(password, 0, password.Length); }
                        if (cancel.IsCancellationRequested)
                            return;
                        if (isPresent(login.SteamId))
                        {
                            feedback.Text = "Этот аккаунт уже подключён на этом ПК.";
                            return;
                        }
                        Display(Step.ConfirmLogin);
                        break;
                    case Step.ConfirmLogin:
                        if (value.Length != 0 && first.Visible)
                        {
                            ulong method = login.Confirmations.Contains(3) ? 3UL : 2UL;
                            await api.SubmitLoginCode(login, value.ToUpperInvariant(), method, cancel);
                        }
                        using (var wait = CancellationTokenSource.CreateLinkedTokenSource(cancel))
                        {
                            wait.CancelAfter(TimeSpan.FromMinutes(3));
                            await api.WaitForLogin(login, wait.Token);
                        }
                        if (!cancel.IsCancellationRequested)
                            Display(Step.SendSms);
                        break;
                    case Step.SendSms:
                        checkVault();
                        await api.StartTransfer(login, cancel);
                        if (!cancel.IsCancellationRequested)
                            Display(Step.EnterSms);
                        break;
                    case Step.EnterSms:
                        pending = await api.FinishTransfer(login, value);
                        Display(Step.Save);
                        FinishSaving();
                        break;
                    case Step.Save:
                        FinishSaving();
                        break;
                }
            }
            catch (Exception error)
            {
                if (stop.IsCancellationRequested)
                    return;
                var steam = error as SteamApiError;
                if (started == Step.EnterSms && pending == null && (steam == null || steam.Result == 16))
                    Display(Step.Uncertain);
                feedback.Text = SteamUi.Error(error);
            }
            finally
            {
                busy = protectCommit = false;
                if (!IsDisposed)
                {
                    next.Enabled = close.Enabled = first.Enabled = second.Enabled = true;
                    qrPanel.Enabled = true;
                    if (started == Step.Login && step == Step.Login)
                        StartQr();
                }
            }
        }

        private void FinishSaving()
        {
            Connected = save(pending);
            pending = null;
            Display(Step.Done);
        }
    }

    internal sealed class RequestsDialog : Form
    {
        private readonly SteamApi api;
        private readonly Account account;
        private readonly ComboBox category;
        private readonly ListBox requests;
        private readonly Button allow;
        private readonly Button decline;
        private readonly Button refresh;
        private readonly Label detail;
        private readonly CancellationTokenSource stop = new CancellationTokenSource();
        private bool busy;
        private bool committing;

        public RequestsDialog(SteamApi api, Account account)
        {
            this.api = api;
            this.account = account;
            SteamUi.StyleDialog(this, "Подтверждения Steam Guard", 600, 414);
            var layout = Theme.Rows(39, 32, 0, 78, 42);
            layout.Padding = new Padding(19);
            Controls.Add(layout);
            layout.Controls.Add(Theme.Label(account.Name, Theme.Account, Theme.Text), 0, 0);
            category = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill, BackColor = Theme.Field,
                ForeColor = Theme.Text, AccessibleName = "Тип подтверждения", Margin = Padding.Empty
            };
            category.Items.Add("Запросы на вход");
            if (account.Registration.IdentitySecret.Length != 0)
                category.Items.Add("Обмены и другие операции Steam");
            category.SelectedIndex = 0;
            category.SelectedIndexChanged += async delegate { await RefreshRequests(null); };
            layout.Controls.Add(category, 0, 1);
            requests = new ListBox
            {
                Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, IntegralHeight = false,
                BackColor = Theme.Field, ForeColor = Theme.Text, AccessibleName = "Запросы Steam"
            };
            requests.SelectedIndexChanged += delegate { ShowDetails(); };
            layout.Controls.Add(requests, 0, 2);
            detail = Theme.Label("", Theme.Body, Theme.Muted);
            detail.AutoEllipsis = false;
            layout.Controls.Add(detail, 0, 3);

            var row = Theme.ButtonRow(false);
            refresh = Theme.Button("Обновить", 108);
            allow = Theme.Button("Подтвердить", 132, true);
            decline = Theme.Button("Отклонить", 120);
            row.Controls.Add(refresh);
            row.Controls.Add(allow);
            row.Controls.Add(decline);
            layout.Controls.Add(row, 0, 4);
            refresh.Click += async delegate { await RefreshRequests(null); };
            allow.Click += async delegate { await Resolve(true); };
            decline.Click += async delegate { await Resolve(false); };
            Shown += async delegate { await RefreshRequests(null); };
            ShowDetails();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (committing)
                e.Cancel = true;
            else
                stop.Cancel();
            base.OnFormClosing(e);
        }

        private void SetBusy(bool value)
        {
            busy = value;
            category.Enabled = requests.Enabled = refresh.Enabled = !value;
            allow.Enabled = decline.Enabled = !value && requests.SelectedItem != null;
        }

        private void ShowDetails()
        {
            var login = requests.SelectedItem as LoginRequest;
            var operation = requests.SelectedItem as CommunityRequest;
            if (login != null)
                detail.Text = login.Details;
            else if (operation != null)
                detail.Text = operation.Details;
            else
                detail.Text = "";
            allow.Enabled = decline.Enabled = !busy && requests.SelectedItem != null;
        }

        private async Task RefreshRequests(string accessToken)
        {
            if (busy)
                return;
            bool logins = category.SelectedIndex == 0;
            SetBusy(true);
            requests.Items.Clear();
            detail.Text = "Получаю запросы Steam…";
            try
            {
                var cancel = stop.Token;
                string token = accessToken ?? await api.AccessToken(account.Registration, cancel);
                object[] entries = logins
                    ? (await api.LoginRequests(token, cancel)).Cast<object>().ToArray()
                    : (await api.CommunityRequests(account.Registration, token, cancel)).Cast<object>().ToArray();
                if (cancel.IsCancellationRequested)
                    return;
                requests.Items.AddRange(entries);
                detail.Text = entries.Length > 0 ? "Выбери запрос, чтобы увидеть детали." : "Новых запросов нет.";
            }
            catch (Exception error)
            {
                if (!stop.IsCancellationRequested)
                    detail.Text = SteamUi.Error(error);
            }
            finally
            {
                if (!IsDisposed)
                    SetBusy(false);
            }
        }

        private async Task Resolve(bool approve)
        {
            object target = requests.SelectedItem;
            if (busy || target == null)
                return;
            string action = approve ? "Подтвердить" : "Отклонить";
            if (MessageBox.Show(this, action + " запрос «" + target + "»?" + Environment.NewLine + detail.Text,
                account.Name, MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                return;

            committing = true;
            SetBusy(true);
            string token;
            try
            {
                token = await api.AccessToken(account.Registration, stop.Token);
                var login = target as LoginRequest;
                if (login != null)
                    await api.ResolveLogin(account, token, login, approve, stop.Token);
                else
                    await api.ResolveCommunity(account.Registration, token, (CommunityRequest)target, approve, stop.Token);
            }
            catch (Exception error)
            {
                detail.Text = SteamUi.Error(error);
                return;
            }
            finally
            {
                committing = false;
                SetBusy(false);
            }
            await RefreshRequests(token);
        }
    }

    internal sealed class RecoveryDialog : Form
    {
        public RecoveryDialog(Account account)
        {
            SteamUi.StyleDialog(this, "Код восстановления Steam Guard", 460, 167);
            var layout = Theme.Rows(38, 36, 0, 38);
            layout.Padding = new Padding(17);
            Controls.Add(layout);
            layout.Controls.Add(Theme.Label(account.Name, Theme.Account, Theme.Text), 0, 0);
            var code = Theme.FieldBox("Код восстановления", 32);
            code.ReadOnly = true;
            code.Text = account.Registration.RecoveryCode;
            layout.Controls.Add(code, 0, 1);
            var row = Theme.ButtonRow(true);
            var close = Theme.Button("Готово", 100, true);
            close.DialogResult = DialogResult.OK;
            row.Controls.Add(close);
            layout.Controls.Add(row, 0, 3);
            AcceptButton = CancelButton = close;
        }
    }
}
