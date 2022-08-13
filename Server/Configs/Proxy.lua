
function LoginFilter(client, request, requestData, targetUrl) : bool
	if request.InputMethod == "POST" then
		local s, e, f = string.find(requestData, "user_name=([%w%.]+)");
		if f == "rd" then
			request.Redirect("/");
			return true;
		end;
	end;
	return false;
end;