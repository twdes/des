const LogMessageScopeProxy typeof TecWare.DE.Stuff.LogMessageScopeProxy

function Run()
	Log.Info("Executed.");
end;

local function returnUser() : table
	local user = TryGetCurrentUser();
	if user == nil then
		return { User = "no" };
	else
		return { User = user.Identity.Name };
	end;
end;

Actions["err"] = {
	{ Name = "log" },
	{ Name = "msg" },

	Method = function (log : LogMessageScopeProxy, msg : string) : table
		log:WriteLine("Test");
		error("Error öß");
		return {};
	end
};

Actions["tab"] = {
	Method = function (t : table) : table
		return t;
	end
};

Actions["user"] = {
	Security = "desUser",

	Method = returnUser
};

Actions["noneuser"] = {
	Security = nil,

	Method = returnUser
};