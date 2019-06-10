/// The MIT License (MIT)
/// Copyright (c) 2016 Bazinga Technologies Inc

namespace FSharp.Data.GraphQL

open System
open System.Collections.Generic
open System.Net.Http
open FSharp.Data.GraphQL
open FSharp.Data.GraphQL.Client
open System.Text
open System.Reflection
open ReflectionPatterns

/// The connection component for GraphQLClient module.
type GraphQLClientConnection() =
    let client = new HttpClient()
    member internal __.Client = client
    interface IDisposable with
        member __.Dispose() = client.Dispose()

/// A requrest object for making GraphQL calls using the GraphQL client module.
type GraphQLRequest  =
      /// Gets the URL of the GraphQL server which will be called.
    { ServerUrl : string
      /// Gets custom HTTP Headers to pass with each call using this request.
      HttpHeaders: seq<string * string>
      /// Gets the name of the operation that should run on the server.
      OperationName : string option
      /// Gets the query string which should be executed on the GraphQL server.
      Query : string
      /// Gets variables to be sent with the query.
      Variables : (string * obj) [] }

/// Executes calls to GraphQL servers and return their responses.
module GraphQLClient =
    let private ensureSuccessCode (response : Async<HttpResponseMessage>) =
        async {
            let! response = response
            return response.EnsureSuccessStatusCode()
        }

    let private addHeaders (httpHeaders : seq<string * string>) (requestMessage : HttpRequestMessage) =
        if not (isNull httpHeaders)
        then httpHeaders |> Seq.iter (fun (name, value) -> requestMessage.Headers.Add(name, value))

    let private postAsync (client : HttpClient) (serverUrl : string) (httpHeaders : seq<string * string>) (content : HttpContent) =
        async {
            use requestMessage = new HttpRequestMessage(HttpMethod.Post, serverUrl)
            requestMessage.Content <- content
            addHeaders httpHeaders requestMessage
            let! response = client.SendAsync(requestMessage) |> Async.AwaitTask |> ensureSuccessCode
            let! content = response.Content.ReadAsStringAsync() |> Async.AwaitTask
            return content
        }

    let private getAsync (client : HttpClient) (serverUrl : string) =
        async {
            let! response = client.GetAsync(serverUrl) |> Async.AwaitTask |> ensureSuccessCode
            let! content = response.Content.ReadAsStringAsync() |> Async.AwaitTask
            return content
        }

    /// Sends a request to a GraphQL server asynchronously.
    let sendRequestAsync (connection : GraphQLClientConnection) (request : GraphQLRequest) =
        async {
            let client = connection.Client
            let variables = 
                match request.Variables with
                | null | [||] -> JsonValue.Null
                | _ -> Map.ofArray request.Variables |> Serialization.toJsonValue
            let operationName =
                match request.OperationName with
                | Some x -> JsonValue.String x
                | None -> JsonValue.Null
            let requestJson =         
                [| "operationName", operationName
                   "query", JsonValue.String request.Query
                   "variables", variables |]
                |> JsonValue.Record
            use content = new StringContent(requestJson.ToString(), Encoding.UTF8, "application/json")
            return! postAsync client request.ServerUrl request.HttpHeaders content
        }
    
    /// Sends a request to a GraphQL server.
    let sendRequest client request =
        sendRequestAsync client request
        |> Async.RunSynchronously

    /// Executes an introspection schema request to a GraphQL server asynchronously.
    let sendIntrospectionRequestAsync (connection : GraphQLClientConnection) (serverUrl : string) httpHeaders =
        let sendGet() = async { return! getAsync connection.Client serverUrl }
        let rethrow (exns : exn list) =
            let rec mapper (acc : string) (exns : exn list) =
                let aggregateMapper (ex : AggregateException) = mapper "" (List.ofSeq ex.InnerExceptions)
                match exns with
                | [] -> acc.TrimEnd()
                | ex :: tail ->
                    match ex with
                    | :? AggregateException as ex -> mapper (acc + aggregateMapper ex + " ") tail
                    | ex -> mapper (acc + ex.Message + " ") tail
            failwithf "Failure trying to recover introspection schema from server at \"%s\". Errors: %s" serverUrl (mapper "" exns)
        async {
            try return! sendGet()
            with getex ->
                let request =
                    { ServerUrl = serverUrl
                      HttpHeaders = httpHeaders
                      OperationName = None
                      Query = Introspection.IntrospectionQuery
                      Variables = [||] }
                try return! sendRequestAsync connection request
                with postex -> return rethrow [getex; postex]
        }

    /// Executes an introspection schema request to a GraphQL server.
    let sendIntrospectionRequest client serverUrl httpHeaders = 
        sendIntrospectionRequestAsync client serverUrl httpHeaders
        |> Async.RunSynchronously

    /// Executes a multipart request to a GraphQL server asynchronously.
    let sendMultipartRequestAsync (connection : GraphQLClientConnection) (request : GraphQLRequest) =
        async {
            let client = connection.Client
            let boundary = sprintf "----GraphQLProviderBoundary%s" (Guid.NewGuid().ToString("N"))
            use content = new MultipartContent("form-data", boundary)
            let files = 
                let rec chooser (name: string, value : obj) =
                    match value with
                    | null | :? string -> None
                    | :? Upload as x -> Some [|name, x|]
                    | OptionValue x -> x |> Option.bind (fun x -> chooser (name, x))
                    | :? IDictionary<string, obj> as x ->
                        x |> Seq.choose (fun kvp -> chooser (sprintf "%s.%s" name (kvp.Key.FirstCharLower()), kvp.Value)) 
                          |> Seq.collect id
                          |> Array.ofSeq
                          |> Some
                    | EnumerableValue x -> 
                        x |> Array.mapi (fun ix x -> chooser (sprintf "%s.%i" name ix, x))
                          |> Array.choose id // We can't choose directly with chooser because we need to know the original index for each item
                          |> Array.collect id
                          |> Some
                    | _ -> None
                request.Variables
                |> Array.choose chooser
                |> Array.collect id
            use operationContent = 
                let variables = 
                    match request.Variables with
                    | null | [||] -> JsonValue.Null
                    | _ -> request.Variables |> Map.ofArray |> Serialization.toJsonValue
                let operationName =
                    match request.OperationName with
                    | Some x -> JsonValue.String x
                    | None -> JsonValue.Null
                let json =
                    [| "operationName", operationName
                       "query", JsonValue.String request.Query
                       "variables", variables |]
                    |> JsonValue.Record
                let content = new StringContent(json.ToString(JsonSaveOptions.DisableFormatting))
                content.Headers.Add("Content-Disposition", "form-data; name=\"operations\"")
                content
            content.Add(operationContent)
            use mapContent =
                let files =
                    files
                    |> Array.mapi (fun ix (name, _) -> ix.ToString(), JsonValue.Array [| JsonValue.String (sprintf "variables.%s" name) |])
                    |> JsonValue.Record
                let content = new StringContent(files.ToString(JsonSaveOptions.DisableFormatting))
                content.Headers.Add("Content-Disposition", "form-data; name=\"map\"")
                content
            content.Add(mapContent)
            let fileContents =
                files
                |> Array.mapi (fun ix (_, value) ->
                    let content = new StreamContent(value.Stream)
                    content.Headers.Add("Content-Disposition", sprintf "form-data; name=\"%i\"; filename=\"%s\"" ix value.FileName)
                    content.Headers.Add("Content-Type", value.ContentType)
                    content)
            fileContents |> Array.iter content.Add
            return! postAsync client request.ServerUrl request.HttpHeaders content
        }

    /// Executes a multipart request to a GraphQL server.
    let sendMultipartRequest connection request =
        sendMultipartRequestAsync connection request
        |> Async.RunSynchronously