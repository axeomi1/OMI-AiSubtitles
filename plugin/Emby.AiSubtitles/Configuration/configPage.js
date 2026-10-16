define(['baseView', 'loading', 'emby-input', 'emby-button', 'emby-checkbox', 'emby-select'], function (BaseView, loading) {
    'use strict';

    var pluginId = '473f36ab-1339-419d-983f-54b264390fc3';

    var textFields = ['WhisperApiUrl', 'WhisperApiKey', 'WhisperModel', 'SourceLanguage',
        'LlmApiUrl', 'LlmApiKey', 'LlmModel', 'SubtitleMode', 'SubtitleFormat',
        'PrimaryTextColor', 'SecondaryTextColor', 'FfmpegPath'];
    var numberFields = ['TranslationBatchSize', 'MaxConcurrentJobs', 'RequestTimeoutMinutes'];
    var boolFields = ['EnableTranslation', 'SaveNextToMedia', 'AllowAutomaticDownload', 'FilterFillerSounds'];

    function escapeHtml(str) {
        return String(str || '')
            .replace(/&/g, '&amp;')
            .replace(/</g, '&lt;')
            .replace(/>/g, '&gt;')
            .replace(/"/g, '&quot;');
    }

    function formatDuration(sec) {
        sec = Math.max(0, parseInt(sec, 10) || 0);
        var m = Math.floor(sec / 60);
        var s = sec % 60;
        return m > 0 ? (m + '分' + s + '秒') : (s + '秒');
    }

    function renderJobs(view, jobs) {
        var container = view.querySelector('#jobsContainer');
        if (!container) {
            return;
        }
        if (!jobs || !jobs.length) {
            container.innerHTML = '<div class="fieldDescription">当前没有正在运行或历史字幕生成任务。在影片「编辑字幕 → 搜索」中点击下载即可发起任务。</div>';
            return;
        }

        var html = '<div style="display:flex;flex-direction:column;gap:0.75em;">';
        for (var i = 0; i < jobs.length; i++) {
            var j = jobs[i];
            var barColor = '#2196F3';
            if (j.State === 'Completed') {
                barColor = '#4CAF50';
            } else if (j.State === 'Failed') {
                barColor = '#F44336';
            } else if (j.State === 'Queued') {
                barColor = '#9E9E9E';
            }

            html += '<div style="padding:0.75em 0.9em;border-radius:6px;background:rgba(0,0,0,0.2);border:1px solid rgba(255,255,255,0.08);">';
            html += '<div style="display:flex;justify-content:space-between;align-items:center;gap:0.5em;flex-wrap:wrap;margin-bottom:0.35em;">';
            html += '<div style="font-weight:600;">' + escapeHtml(j.Title || j.FileName) + ' <span style="opacity:0.7;font-weight:normal;font-size:0.88em;">[' + escapeHtml(j.Language) + ' · ' + escapeHtml(j.Format || 'ASS') + ']</span></div>';
            html += '<div style="font-size:0.88em;opacity:0.85;">' + escapeHtml(j.StateText) + ' · 耗时 ' + formatDuration(j.ElapsedSeconds) + '</div>';
            html += '</div>';

            html += '<div style="height:6px;width:100%;background:rgba(255,255,255,0.12);border-radius:3px;overflow:hidden;">';
            html += '<div style="height:100%;width:' + Math.min(100, Math.max(2, j.Percent || 0)) + '%;background:' + barColor + ';transition:width 0.3s;"></div>';
            html += '</div>';

            if (j.FileName) {
                html += '<div style="font-size:0.8em;opacity:0.65;margin-top:0.35em;word-break:break-all;">文件：' + escapeHtml(j.FileName) + '（创建于 ' + escapeHtml(j.CreatedTime) + '）</div>';
            }
            if (j.Error) {
                html += '<div style="font-size:0.85em;color:#ff8a80;margin-top:0.35em;word-break:break-all;">错误：' + escapeHtml(j.Error) + '</div>';
            }
            html += '</div>';
        }
        html += '</div>';
        container.innerHTML = html;
    }

    function refreshJobs(view) {
        var url = ApiClient.getUrl('AiSubtitles/Jobs');
        ApiClient.getJSON(url).then(function (res) {
            renderJobs(view, res && res.Jobs ? res.Jobs : []);
        }).catch(function () {
            // ignore transient polling errors
        });
    }

    function clearJobs(view) {
        var url = ApiClient.getUrl('AiSubtitles/Jobs/Clear');
        ApiClient.ajax({ type: 'POST', url: url, dataType: 'json' }).then(function () {
            refreshJobs(view);
        });
    }

    function loadConfig(view) {
        loading.show();
        ApiClient.getPluginConfiguration(pluginId).then(function (config) {
            textFields.forEach(function (f) { view.querySelector('#' + f).value = config[f] || ''; });
            if (!view.querySelector('#SubtitleFormat').value) {
                view.querySelector('#SubtitleFormat').value = 'ass';
            }
            if (!view.querySelector('#PrimaryTextColor').value) {
                view.querySelector('#PrimaryTextColor').value = '#FFFFFF';
            }
            if (!view.querySelector('#SecondaryTextColor').value) {
                view.querySelector('#SecondaryTextColor').value = '#FFD700';
            }
            numberFields.forEach(function (f) { view.querySelector('#' + f).value = config[f]; });
            boolFields.forEach(function (f) { view.querySelector('#' + f).checked = !!config[f]; });
            loading.hide();
        });
    }

    function saveConfig(view) {
        loading.show();
        ApiClient.getPluginConfiguration(pluginId).then(function (config) {
            textFields.forEach(function (f) { config[f] = view.querySelector('#' + f).value.trim(); });
            numberFields.forEach(function (f) { config[f] = parseInt(view.querySelector('#' + f).value, 10) || 1; });
            boolFields.forEach(function (f) { config[f] = view.querySelector('#' + f).checked; });
            ApiClient.updatePluginConfiguration(pluginId, config).then(function (result) {
                Dashboard.processPluginConfigurationUpdateResult(result);
            });
        });
    }

    function View(view, params) {
        BaseView.apply(this, arguments);
        var self = this;

        view.querySelector('.aiSubtitlesConfigForm').addEventListener('submit', function (e) {
            e.preventDefault();
            saveConfig(view);
            return false;
        });

        var btnRefresh = view.querySelector('#btnRefreshJobs');
        if (btnRefresh) {
            btnRefresh.addEventListener('click', function () {
                refreshJobs(view);
            });
        }

        var btnClear = view.querySelector('#btnClearJobs');
        if (btnClear) {
            btnClear.addEventListener('click', function () {
                clearJobs(view);
            });
        }

        self._pollTimer = null;
    }

    Object.assign(View.prototype, BaseView.prototype);

    View.prototype.onResume = function (options) {
        BaseView.prototype.onResume.apply(this, arguments);
        var view = this.view;
        loadConfig(view);
        refreshJobs(view);
        if (this._pollTimer) {
            clearInterval(this._pollTimer);
        }
        this._pollTimer = setInterval(function () {
            refreshJobs(view);
        }, 2000);
    };

    View.prototype.onPause = function () {
        if (this._pollTimer) {
            clearInterval(this._pollTimer);
            this._pollTimer = null;
        }
        BaseView.prototype.onPause.apply(this, arguments);
    };

    return View;
});
