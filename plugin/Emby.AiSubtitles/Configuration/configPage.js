define(['baseView', 'loading', 'emby-input', 'emby-button', 'emby-checkbox', 'emby-select'], function (BaseView, loading) {
    'use strict';

    var pluginId = '473f36ab-1339-419d-983f-54b264390fc3';

    var textFields = ['WhisperApiUrl', 'WhisperApiKey', 'WhisperModel', 'SourceLanguage',
        'LlmApiUrl', 'LlmApiKey', 'LlmModel', 'SubtitleMode', 'FfmpegPath'];
    var numberFields = ['TranslationBatchSize', 'MaxConcurrentJobs', 'RequestTimeoutMinutes'];
    var boolFields = ['EnableTranslation', 'SaveNextToMedia', 'AllowAutomaticDownload'];

    function loadConfig(view) {
        loading.show();
        ApiClient.getPluginConfiguration(pluginId).then(function (config) {
            textFields.forEach(function (f) { view.querySelector('#' + f).value = config[f] || ''; });
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
        view.querySelector('.aiSubtitlesConfigForm').addEventListener('submit', function (e) {
            e.preventDefault();
            saveConfig(view);
            return false;
        });
    }

    Object.assign(View.prototype, BaseView.prototype);

    View.prototype.onResume = function (options) {
        BaseView.prototype.onResume.apply(this, arguments);
        loadConfig(this.view);
    };

    return View;
});
