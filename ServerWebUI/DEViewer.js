/// <reference path="libs/jquery.d.ts" />
var __extends = (this && this.__extends) || (function () {
    var extendStatics = function (d, b) {
        extendStatics = Object.setPrototypeOf ||
            ({ __proto__: [] } instanceof Array && function (d, b) { d.__proto__ = b; }) ||
            function (d, b) { for (var p in b) if (b.hasOwnProperty(p)) d[p] = b[p]; };
        return extendStatics(d, b);
    }
    return function (d, b) {
        extendStatics(d, b);
        function __() { this.constructor = d; }
        d.prototype = b === null ? Object.create(b) : (__.prototype = b.prototype, new __());
    };
})();
function loadTemplate(template, node) {
    var html = template.html();
    html = html.replace(/\{\{([A-Z0-9]|\/)+\}\}/gim, function (key) {
        var key = key.substring(2, key.length - 2);
        var pos = key.indexOf('/');
        if (pos == -1) {
            if (key === "image") {
                return 'src="' + node.attr(key) + '"'; // do not load {{image}}
            }
            else
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
function info(message) {
    var args = [];
    for (var _i = 1; _i < arguments.length; _i++) {
        args[_i - 1] = arguments[_i];
    }
    if (console != null) {
        console.log(message, args);
    }
} // info
var DETab = /** @class */ (function () {
    function DETab(app, viewId) {
        var _this = this;
        this.app = app;
        this.viewId = viewId;
        this.visible = true;
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
    DETab.prototype.showTab = function (visible) {
        this.visible = visible;
        this.button.css('display', visible ? 'inline' : 'none');
    }; // showTab
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
    Object.defineProperty(DETab.prototype, "IsVisible", {
        get: function () { return this.visible; },
        enumerable: true,
        configurable: true
    });
    return DETab;
}()); // class DETab
var DELogTab = /** @class */ (function (_super) {
    __extends(DELogTab, _super);
    function DELogTab(app) {
        var _this = _super.call(this, app, "tabLog") || this;
        _this.onLoading = false;
        _this.currentLines = 0; // current loaded lines
        _this.totalLines = 0; // last updatedtotal lines
        _this.newTotalLines = 0; // per event notified total lines
        _this.fetchLogButton = $('#fetchAllAction');
        _this.fetchLogButton.css("visibility", "collapse");
        _this.fetchLogButton.click((function () {
            this.reload2(true);
        }).bind(_this));
        return _this;
    } // ctor
    DELogTab.prototype.unselect = function () {
        _super.prototype.unselect.call(this);
    }; // select
    DELogTab.prototype.reload = function (url) {
        _super.prototype.reload.call(this, url);
        this.currentUrl = url;
        this.reload2(false);
    }; // reload
    DELogTab.prototype.formatLogLine = function (lineStamp, lineType, lineText) {
        var timeString = lineStamp.toLocaleString('de', { hour: '2-digit', minute: '2-digit', second: '2-digit', hour12: false });
        return [
            '<tr>',
            '<td class="logLineTime logLineBk', lineType, '">', timeString, ',', lineStamp.getMilliseconds().toLocaleString('de', { minimumIntegerDigits: 3 }), '</td>',
            '<td class="logLineCell"><div class="logLineText logLineTextSingle">', lineText, '</div></td>',
            '</tr>'
        ].join("");
    }; // formatLogLine
    DELogTab.prototype.appendToggleEvents = function (lines) {
        $('.logLineTime', lines).click(function () {
            var text = $('.logLineText', $(this).parent());
            if (text.hasClass('logLineTextFull')) {
                text.scrollTop(0);
            }
            text.toggleClass("logLineTextFull");
            text.toggleClass("logLineTextSingle");
        });
    }; // appendToggleEvents
    DELogTab.prototype.updateStatusBar = function () {
        var currentCount = $('#currentCount', this.RootElement);
        currentCount.text(this.currentLines.toLocaleString() + " Lines");
        if (this.totalLines > this.currentLines) {
            this.fetchLogButton.css("visibility", "visible");
            this.fetchLogButton.text("Read (" + this.totalLines.toLocaleString() + ")");
        }
    }; // updateFetchAllButton
    DELogTab.prototype.reload2 = function (all) {
        var firstDate = null;
        var tabItems = $('#tabItems', this.RootElement);
        if (this.onLoading)
            return;
        tabItems.empty();
        if (!this.IsVisible)
            return;
        this.onLoading = true;
        var state = $('#state', this.RootElement);
        state.text('Loading...');
        this.fetchLogButton.css("visibility", "collapse");
        var listGetCommand = "?action=listget&id=tw_lines";
        if (!all) {
            listGetCommand += "&start=-100"; // last 100
        }
        this.App.serverGet(this.currentUrl + listGetCommand, (function (data) {
            var innerHtmlElements = new Array();
            //var startAt = Number($('items', data).attr('s'))
            this.currentLines = Number($('items', data).attr('c'));
            this.totalLines = Number($('items', data).attr('tc'));
            state.text('Parse ' + this.currentLines.toString() + '...');
            $('items > line', data).get().reverse().forEach((function (element) {
                var line = $(element);
                var lineType = line.attr('typ');
                var lineStamp = new Date(Date.parse(line.attr('stamp')));
                var lineText = line.text().replace(/\</g, '&lt;').replace(/\>/g, '&gt;');
                if (firstDate == null || firstDate != lineStamp.getDate()) { // add seperator
                    innerHtmlElements.push(['<tr><td colspan="2" class="logLineHeader">Datum: ', lineStamp.toLocaleDateString(), '</td></tr>']);
                    firstDate = lineStamp.getDate();
                }
                innerHtmlElements.push(this.formatLogLine(lineStamp, lineType, lineText));
            }).bind(this));
            // create html elements
            var lines = $(innerHtmlElements.join(""));
            // activate toggle
            this.appendToggleEvents(lines);
            // update view
            tabItems.append(lines);
            // set the the fetch all button
            this.updateStatusBar();
            this.RootElement.scrollTop(0);
        }).bind(this), (function () {
            state.text('Done.');
            this.onLoading = false;
            this.appendLines();
        }).bind(this));
    }; // reload
    DELogTab.prototype.appendLines = function () {
        if (this.onLoading) // do not load lines, durring full load
            return;
        // are there lines to append
        var diff = this.newTotalLines - this.totalLines;
        if (diff <= 0)
            return;
        var tabItems = $('#tabItems', this.RootElement);
        var listGetCommand = "?action=listget&id=tw_lines&start=" + this.totalLines + "&count=" + diff;
        this.onLoading = true;
        this.App.serverGet(this.currentUrl + listGetCommand, 
        // -- fnRet --
        (function (data) {
            var innerHtmlElements = new Array();
            this.totalLines = this.newTotalLines; // update totalLines
            this.currentLines = this.currentLines + diff;
            $('items > line', data).get().reverse().forEach((function (element) {
                var line = $(element);
                innerHtmlElements.push(this.formatLogLine(new Date(Date.parse(line.attr('stamp'))), line.attr('typ'), line.text().replace(/\</g, '&lt;').replace(/\>/g, '&gt;')));
            }).bind(this));
            // create html elements
            var lines = $(innerHtmlElements.join(""));
            // activate toggle
            this.appendToggleEvents(lines);
            $('.logLineTime', lines).fadeIn(700);
            $('.logLineCell', lines).fadeIn(700);
            // update view
            var firstHeader = $('.logLineHeader', tabItems).first();
            if (firstHeader == null)
                tabItems.append(lines);
            else
                lines.insertAfter(firstHeader.parent());
            // set the the fetch all button
            this.updateStatusBar();
        }).bind(this), 
        // -- fnFinish --
        (function () {
            this.onLoading = false;
            this.appendLines();
        }).bind(this));
    }; // appendLines
    DELogTab.prototype.updateLines = function (data) {
        var lines = $('lines', data);
        this.newTotalLines = Number(lines.attr('lineCount'));
        this.appendLines();
    }; // updateLine
    return DELogTab;
}(DETab)); // class DELogTab
var DEProperties = /** @class */ (function (_super) {
    __extends(DEProperties, _super);
    function DEProperties(app) {
        return _super.call(this, app, "tabProperties") || this;
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
    DEProperties.prototype.updateProperty = function (propId, value) {
        var td = $('#' + propId, this.RootElement);
        td.html(formatValue(td.attr('type'), td.attr('format'), value));
    }; // updateProperty
    return DEProperties;
}(DETab)); // class DEProperties
var DEConfig = /** @class */ (function (_super) {
    __extends(DEConfig, _super);
    function DEConfig(app) {
        return _super.call(this, app, "tabConfig") || this;
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
}(DETab)); // class DEConfig
var DEServerInfo = /** @class */ (function (_super) {
    __extends(DEServerInfo, _super);
    function DEServerInfo(app) {
        return _super.call(this, app, "tabInfo") || this;
    } // ctor
    DEServerInfo.prototype.reload = function (url) {
        _super.prototype.reload.call(this, url);
        var serverInfoElement = $('#serverInfo', this.RootElement);
        var dumpFilesElement = $('#dumpList', this.RootElement);
        serverInfoElement.empty();
        dumpFilesElement.empty();
        if (!this.IsVisible)
            return;
        // get server info
        this.App.serverGet("?action=serverinfo", (function (data) {
            var serverInfo = $(':root', data);
            serverInfoElement.append(loadTemplate($('#serverInfoTemplate'), serverInfo));
            var tmpl = $('#assemblyInfoTemplate');
            $('assembly', data).each(function (index, element) {
                serverInfoElement.append(loadTemplate(tmpl, $(element)));
            });
        }));
        this.App.serverGet("?action=listget&id=tw_dumpfiles", function (data) {
            var dumpAdded = false;
            $('items > dump', data).each(function (index, element) {
                var dump = $(element);
                var id = dump.attr("id");
                var created = new Date(Date.parse(dump.attr("created")));
                dumpFilesElement.append([
                    '<li><a href="/?action=dumpload&id=', id, '">',
                    created.toLocaleString(),
                    ' (', formatValue('long', 'FILESIZE', dump.attr('size')), ')</a></li>'
                ].join(""));
                dumpAdded = true;
            });
            $('#dumpListFrame').css('display', dumpAdded ? 'block' : 'none');
        });
    }; // reload
    return DEServerInfo;
}(DETab)); // class DEServerInfo
var DEViewer = /** @class */ (function () {
    function DEViewer() {
        this.onReloading = false;
        this.currentUri = ""; // selected uri
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
        this.startEventListener(); // start event listener to monitor the connection to the server
        this.beginReloadIndex(); // start also load of index
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
            alert("Could not execute command.\nError: " + errorThrown);
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
        this.updateStatus("Loading...");
        var obj = this;
        this.serverGet('?action=list', function (data) {
            obj.updateIndex(data);
        }, function () {
            obj.onReloading = false;
            setTimeout(function () { return obj.refreshActionElement.toggleClass("actionButton", true); }, 100);
        });
    }; // beginReloadIndex
    DEViewer.prototype.updateIndexAppend = function (current, url, level) {
        // get the attributes
        var name = current.attr('name');
        var displayname = current.attr('displayname');
        var icon = current.attr('icon');
        // check if a log exists
        var hasLog = $('list[id="tw_lines"]', current).first().length > 0;
        if (name === 'Main') {
            name = "";
            displayname = "Data Exchange Server";
        }
        else
            url = url + name + '/';
        // insert the option
        this.currentNodeElement.append(['<option name="', name, '" uri="', url, '" icon="', icon, '" hasLog="', hasLog, '">', level, displayname, '</option>'].join(''));
        // insert children
        var obj = this;
        level = level + "&nbsp;&nbsp;&nbsp;";
        current.children('item').each(function (index, element) {
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
            // set the new node
            this.currentUri = option.attr('uri');
            this.updateStatus(null);
            this.currentImageElement.attr("src", option.attr("icon"));
            var hasLog = option.attr('hasLog') === "true";
            // reload tabs
            this.tabs[0].showTab(hasLog);
            this.tabs[3].showTab(this.currentUri === "");
            for (var i = 0; i < this.tabs.length; i++) {
                this.tabs[i].reload(this.currentUri);
            }
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
        }).bind(this), 200);
    }; // beginRefreshUri
    DEViewer.prototype.updateStatus = function (state) {
        if (state == null) {
            state = this.currentHost + this.currentUri;
        }
        this.currentUriElement.text(state);
    }; // updateStatus
    /*
     * -- Event Listener --
     * Controls the connection to the server, and updates the triggers updates to the ui.
     * - the notification is started, with startEventListener
     */
    DEViewer.prototype.startEventListener = function () {
        // exchange the http to ws (it also changes https to wss)
        var wsHost = "ws" + this.currentHost.substring(4);
        // create the WebSocket
        info("Connection to %s", wsHost);
        this.updateStatus("Connecting...");
        this.currentWebSocket = new WebSocket(wsHost, "des_event");
        this.currentWebSocket.onopen = this.eventListenerOnOpen.bind(this);
        this.currentWebSocket.onmessage = this.eventListenerMessage.bind(this);
        this.currentWebSocket.onclose = this.eventListenerOnClose.bind(this);
    }; // startEventListener
    DEViewer.prototype.eventListenerMessage = function (ev) {
        if (ev.type === "message") {
            var d = $(':root', $.parseXML(ev.data));
            //info("raw event: %s", ev.data);
            // check the path
            if (this.currentUri === d.attr('path').substring(1)) {
                var eventId = d.attr('event');
                if (eventId === 'tw_properties')
                    this.tabs[1].updateProperty(d.attr('index'), d.text());
                else if (eventId === 'tw_lines') {
                    this.tabs[0].updateLines(d);
                }
            }
        }
    }; // eventListenerMessage
    DEViewer.prototype.eventListenerOnOpen = function () {
        // notify ready state
        info("Connection opened.");
        this.updateStatus(null);
        // refresh index, content
        this.beginReloadIndex();
    }; // eventListenerOnOpen
    DEViewer.prototype.eventListenerOnClose = function (ev) {
        // connection lost, reconnect to server
        info("Connection closed: %s", ev);
        // reconnect to server
        setTimeout(this.startEventListener.bind(this), 5000);
    }; // eventListenerOnClose
    DEViewer.prototype.stopEventListener = function () {
        var tmp = this.currentWebSocket;
        this.currentWebSocket = null;
        if (tmp != null)
            tmp.close();
    }; // stopEventListener
    Object.defineProperty(DEViewer.prototype, "TabBarElement", {
        get: function () { return this.tabsElement; },
        enumerable: true,
        configurable: true
    });
    return DEViewer;
}()); // class DEViewer
var desViewer = new DEViewer();
$(document).ready(function (e) { return desViewer.init(); });
//# sourceMappingURL=DEViewer.js.map