// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Fredrik Olsson

namespace BorsdataMcp;

public sealed class BorsdataOptions
{
    public string ApiKey { get; set; } = string.Empty;

    public string BaseUrl { get; set; } = "https://apiservice.borsdata.se/v1/";
}
