const LogMessageScopeProxy typeof TecWare.DE.Stuff.LogMessageScopeProxy

function Run()
	Log.Info("Executed.");
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
}