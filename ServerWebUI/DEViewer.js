/// <reference path="libs/jquery.d.ts" />
var __extends = (this && this.__extends) || function (d, b) {
    for (var p in b) if (b.hasOwnProperty(p)) d[p] = b[p];
    function __() { this.constructor = d; }
    d.prototype = b === null ? Object.create(b) : (__.prototype = b.prototype, new __());
};
function loadTemplate(template, node) {
    var html = template.html();
    html = html.replace(/\{\{([A-Z0-9]|\/)+\}\}/gim, function (key) {
        var key = key.substring(2, key.length - 2);
        var pos = key.indexOf('/');
        if (pos == -1) {
            return node.attr(key);
        }
        else {
            var c = $(key.substring(0, pos), node);
            return c.attr(key.substring(pos + 1));
        }
    });
    return html;
} // loadTemplate
function formatNumber(n, digits) {
    return n.toLocaleString(['de'], { minimumFractionDigits: digits, maximumFractionDigits: digits });
} // formatNumber
function formatValue(type, format, rawValue) {
    if (format == "FILESIZE") {
        var n = parseInt(rawValue);
        if (n < 2048)
            return formatNumber(n, 0) + " bytes";
        else if (n < 2097152)
            return formatNumber(n / 1024, 0) + " KiB";
        else if (n < 1073741824)
            return formatNumber(n / 1048576, 1) + " MiB";
        else
            return formatNumber(n / 1073741824, 1) + " GiB";
    }
    else if (format === "{0:N0}")
        return formatNumber(parseInt(rawValue), 0);
    else if (format === "{0:N1}")
        return formatNumber(parseInt(rawValue), 1);
    else
        return rawValue;
} // formatValue
var DETab = (function () {
    function DETab(app, viewId) {
        var _this = this;
        this.app = app;
        this.viewId = viewId;
        // init button
        this.button = $(['span[vid="', this.viewId, '"]'].join(""), app.TabBarElement).first();
        this.button.click(function (e) { return _this.app.selectTab(_this); });
        // init root
        this.root = $('#' + viewId).first();
    } // ctor
    DETab.prototype.select = function () {
        this.button.removeClass('tabunselected');
        this.button.addClass('tabselected');
        this.root.css("display", "block");
    }; // select
    DETab.prototype.unselect = function () {
        this.button.removeClass('tabselected');
        this.button.addClass('tabunselected');
        this.root.css("display", "none");
    }; // unselect
    DETab.prototype.reload = function (url) {
    }; // reload
    Object.defineProperty(DETab.prototype, "App", {
        get: function () { return this.app; },
        enumerable: true,
        configurable: true
    });
    Object.defineProperty(DETab.prototype, "ViewId", {
        get: function () { return this.viewId; },
        enumerable: true,
        configurable: true
    });
    Object.defineProperty(DETab.prototype, "ButtonElement", {
        get: function () { return this.button; },
        enumerable: true,
        configurable: true
    });
    Object.defineProperty(DETab.prototype, "RootElement", {
        get: function () { return this.root; },
        enumerable: true,
        configurable: true
    });
    return DETab;
})(); // class DETab
var DELogTab = (function (_super) {
    __extends(DELogTab, _super);
    function DELogTab(app) {
        _super.call(this, app, "tabLog");
    }
    DELogTab.prototype.reload = function (url) {
        _super.prototype.reload.call(this, url);
        var firstDate = null;
        this.RootElement.empty();
        this.App.serverGet(url + "?action=listget&id=tw_lines", (function (data) {
            $('items > line', data).each((function (index, element) {
                var line = $(element);
                var lineType = line.attr('typ');
                var lineStamp = new Date(Date.parse(line.attr('stamp')));
                var lineText = line.text().replace(/\</g, '&lt;').replace(/\>/g, '&gt;');
                if (firstDate == null || firstDate != lineStamp.getDate()) {
                    this.RootElement.append(['<tr><td colspan="2" class="logLineHeader">Datum: ', lineStamp.toLocaleDateString(), '</td></tr>'].join(""));
                    firstDate = lineStamp.getDate();
                }
                this.RootElement.append([
                    '<tr>',
                    '<td class="logLineTime logLineBk', lineType, '">', lineStamp.toLocaleTimeString(), ',', lineStamp.getMilliseconds().toLocaleString('de', { minimumintegerDigits: 3 }), '</td>',
                    '<td class="logLineCell"><div class="logLineText logLineTextSingle">', lineText, '</div></td>',
                    '</tr>'].join(""));
            }).bind(this));
            // aktivate toggle
            $('.logLineTime', this.RootElement).click(function () {
                var text = $('.logLineText', $(this).parent());
                text.toggleClass("logLineTextFull");
                text.toggleClass("logLineTextSingle");
            });
            this.RootElement.scrollTop(this.RootElement.height());
        }).bind(this));
    }; // reload
    return DELogTab;
})(DETab); // class DELogTab
var DEProperties = (function (_super) {
    __extends(DEProperties, _super);
    function DEProperties(app) {
        _super.call(this, app, "tabProperties");
    } // ctor
    DEProperties.prototype.reload = function (url) {
        var rootElement = this.RootElement;
        this.RootElement.empty();
        this.App.serverGet(url + "?action=listget&id=tw_properties", (function (data) {
            // get and sort the properties
            var properties = $('items > property', data).toArray();
            properties.sort(function (_a, _b) {
                var a = $(_a);
                var b = $(_b);
                var r = a.attr('category').localeCompare(b.attr('category'));
                if (r === 0) {
                    r = a.attr('displayname').localeCompare(b.attr('displayname'));
                }
                return r;
            });
            // generate the html
            rootElement.empty();
            var lastCategory = null;
            properties.forEach(function (cur) {
                var property = $(cur);
                var currentCategory = property.attr('category');
                // create the category
                if (currentCategory !== lastCategory) {
                    rootElement.append(['<tr><th colspan="2">', currentCategory, '</th></tr>'].join(""));
                    lastCategory = currentCategory;
                }
                var typeName = property.attr('type');
                var formatString = property.attr('format');
                rootElement.append(['<tr><td>', property.attr('displayname'), '</td><td id="', property.attr('name'), '" class="propertyValue" format="', formatString, '" type="', typeName, '">', formatValue(typeName, formatString, property.text()), '</td></tr>'].join(""));
                rootElement.append(['<tr><td colspan="2" class="propertyDescription">', property.attr('description'), ' (<span class="propertyName">', property.attr('name'), '</span>)</td></tr>'].join(""));
            });
        }).bind(this));
    }; // reload
    return DEProperties;
})(DETab); // class DEProperties
var DEConfig = (function (_super) {
    __extends(DEConfig, _super);
    function DEConfig(app) {
        _super.call(this, app, "tabConfig");
    } // ctor
    DEConfig.prototype.reload = function (url) {
        this.RootElement.empty();
        this.App.serverGet(url + "?action=config", (function (data) {
            this.loadElement(this.RootElement, $(':root', data), 0);
        }).bind(this));
    }; // reload
    DEConfig.prototype.loadElement = function (nodeUI, nodeData, level) {
        nodeUI.append('<div class="configElementBox">');
        var childUI = $('div', nodeUI).last();
        childUI.append('<div class="configHeader">');
        var headerUI = $('div', childUI).last();
        headerUI.append(['<span class="configHeaderTitle">', nodeData.attr('name'), '</span> <span class="configHeaderDoc">', nodeData.attr('documentation'), '</span>'].join(""));
        childUI.append('</div>');
        childUI.append('<table>');
        var attrTable = $('table', childUI).last();
        nodeData.children('attribute').each(function (index, element) {
            var c = $(element);
            attrTable.append(['<tr><td classe="configAttributeTitle">', c.attr('name'), '</td><td class="', c.attr('isDefault') === 'true' ? 'configAttributeDefault' : 'configAttributeValue', '">', c.text(), '</td></tr>'].join(""));
            attrTable.append(['<tr><td colspan="2" class="configAttributeDocumentation">', c.attr('documentation'), '</td></tr>'].join(""));
        });
        childUI.append('</table>');
        var le = this.loadElement.bind(this);
        nodeData.children('element').each(function (index, element) {
            le(childUI, $(element), level + 1);
        });
        nodeUI.append('</div>');
    }; // loadElement
    return DEConfig;
})(DETab); // class DEConfig
var DEServerInfo = (function (_super) {
    __extends(DEServerInfo, _super);
    function DEServerInfo(app) {
        _super.call(this, app, "tabInfo");
    } // ctor
    DEServerInfo.prototype.reload = function (url) {
        _super.prototype.reload.call(this, url);
        this.RootElement.empty();
        this.App.serverGet("?action=serverinfo", (function (data) {
            var serverInfo = $(':root', data);
            this.RootElement.append(loadTemplate($('#serverInfoTemplate'), serverInfo));
            var obj = this;
            var tmpl = $('#assemblyInfoTemplate');
            $('assembly', data).each(function (index, element) {
                obj.RootElement.append(loadTemplate(tmpl, $(element)));
            });
        }).bind(this));
    }; // reload
    return DEServerInfo;
})(DETab); // class DEServerInfo
var DEViewer = (function () {
    function DEViewer() {
        this.onReloading = false;
        this.beginRefreshTimer = -1;
        this.currentPath = $(location).attr('pathname');
        var lastShlash = this.currentPath.lastIndexOf('/');
        if (lastShlash < this.currentPath.length - 1) {
            this.currentPath = this.currentPath.substring(0, lastShlash + 1);
        }
        this.currentHost = $(location).attr('href');
        lastShlash = this.currentHost.lastIndexOf('/');
        if (lastShlash < this.currentHost.length - 1) {
            this.currentHost = this.currentHost.substring(0, lastShlash + 1);
        }
    } // ctor
    DEViewer.prototype.init = function () {
        var _this = this;
        this.currentNodeElement = $('#currentNode');
        this.currentUriElement = $('#currentUri');
        this.currentImageElement = $('#currentImage');
        this.refreshActionElement = $('#refreshAction');
        this.tabsElement = $('#tabs');
        this.tabs = [
            new DELogTab(this),
            new DEProperties(this),
            new DEConfig(this),
            new DEServerInfo(this)
        ];
        this.refreshActionElement.click(function (e) { return _this.beginReloadIndex(); });
        this.currentNodeElement.change(function (e) { return _this.beginRefreshUri($(':selected', e.target)); });
        this.selectTab(this.tabs[0]);
        // initialize view empty
        this.currentNodeElement.empty();
        this.beginReloadIndex();
    }; // init
    DEViewer.prototype.serverGet = function (request, fnRet, fnFinish) {
        var url = this.currentPath + request;
        $.ajax({
            type: "GET",
            url: url,
            cache: false
        })
            .done(function (data, status, xhr) {
            try {
                if (fnRet != null)
                    fnRet(data, status, xhr);
            }
            finally {
                if (fnFinish != null)
                    fnFinish();
            }
        })
            .fail(function (jqXHR, textStatus, errorThrown) {
            if (fnFinish != null)
                fnFinish();
            alert("Befehl konnte nicht vom Server verarbeitet werden.\nFehler: " + errorThrown);
        });
    }; // serverGet
    DEViewer.prototype.selectTab = function (tab) {
        if (this.currentTab != tab) {
            this.currentTab = tab;
            if (this.currentTab != null)
                this.currentTab.select();
            for (var i = 0; i < this.tabs.length; i++) {
                var c = this.tabs[i];
                if (c != this.currentTab)
                    c.unselect();
            }
        }
    }; // selectTab
    DEViewer.prototype.beginReloadIndex = function () {
        if (this.onReloading)
            return;
        this.onReloading = true;
        this.refreshActionElement.toggleClass("actionButton", false);
        this.currentUriElement.text("Loading...");
        var obj = this;
        this.serverGet('?action=list', function (data) {
            obj.updateIndex(data);
        }, function () {
            obj.onReloading = false;
            setTimeout(function () { return obj.refreshActionElement.toggleClass("actionButton", true); }, 1000);
        });
    }; // beginReloadIndex
    DEViewer.prototype.updateIndexAppend = function (current, url, level) {
        // check if a log exists
        if ($('list[id="tw_lines"]', current).first().length == 0)
            return;
        // get the attributes
        var name = current.attr('name');
        var displayname = current.attr('displayname');
        var icon = current.attr('icon');
        if (name === 'Main') {
            name = "";
            displayname = "Data Exchange Server";
        }
        else
            url = url + name + '/';
        // insert the option
        this.currentNodeElement.append(['<option name="', name, '" uri="', url, '" icon="', icon, '">', level, displayname, '</option>'].join(''));
        // insert children
        var obj = this;
        level = level + "&nbsp;&nbsp;&nbsp;";
        $(':only-child item', current).each(function (index, element) {
            obj.updateIndexAppend($(element), url, level);
        });
    }; // updateIndexAppend
    DEViewer.prototype.updateIndex = function (data) {
        this.currentNodeElement.empty();
        // parse the nodes
        this.updateIndexAppend($(':root', data), "", "");
        // select the current node
        var cur = this.currentUri === null ? $([]) : $('option[uri="' + this.currentUri + '"]', this.currentNodeElement).first();
        if (cur.length === 0) {
            this.beginRefreshUri($('option', this.currentNodeElement).first());
        }
        else {
            cur.prop('selected', 'true');
            this.beginRefreshUri(cur);
        }
    }; // updateIndex
    DEViewer.prototype.beginRefreshUri = function (option) {
        if (this.beginRefreshTimer != -1)
            clearTimeout(this.beginRefreshTimer);
        var refreshAction = this.refreshActionElement;
        var app = this;
        this.beginRefreshTimer = setTimeout((function () {
            this.currentUri = option.attr('uri');
            this.currentUriElement.text(this.currentHost + this.currentUri);
            this.currentImageElement.attr("src", option.attr("icon"));
            // reload tabs
            for (var i = 0; i < this.tabs.length; i++)
                this.tabs[i].reload(this.currentUri);
            // reload actions
            $('#actions > span[loaded="true"]').remove();
            this.serverGet(this.currentUri + '?action=list&recursive=false', function (data) {
                $('action', data).each(function (index, element) {
                    var c = $(element);
                    refreshAction.before(['<span class="actionButton" loaded="true" actionId="', c.attr('id'), '">', c.attr('displayname'), '</span>'].join(""));
                });
                $('#actions > span[loaded="true"]').click(function (e) {
                    var cmd = $(this);
                    cmd.toggleClass('actionButton', false);
                    app.serverGet([app.currentUri, '?action=', cmd.attr('actionId')].join(""), function (returnData) {
                        var r = $(":first-child", returnData);
                        if (r.attr("status") == "ok") {
                            var text = r.attr("text");
                            if (text != null)
                                alert(text);
                        }
                        else {
                            alert("Aufruf fehlgeschlagen:\n" + r.attr("text"));
                        }
                    }, function () {
                        cmd.toggleClass('actionButton', true);
                    });
                });
            });
        }).bind(this), 500);
    }; // beginRefreshUri
    Object.defineProperty(DEViewer.prototype, "TabBarElement", {
        get: function () { return this.tabsElement; },
        enumerable: true,
        configurable: true
    });
    return DEViewer;
})(); // class DEViewer
var desViewer = new DEViewer();
$(document).ready(function (e) { return desViewer.init(); });
//# sourceMappingURL=DEViewer.js.map